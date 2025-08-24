using TripSplit;
using ClosedXML.Excel;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<SettlementEngine>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/people", (SettlementEngine engine) =>
{
	return Results.Ok(engine.GetPeople());
});

app.MapPost("/api/people", (SettlementEngine engine, PersonDto dto) =>
{
	engine.AddPerson(dto.Name);
	return Results.Ok();
});

app.MapPost("/api/people/import", async (SettlementEngine engine, HttpRequest req) =>
{
	using var reader = new StreamReader(req.Body);
	var text = await reader.ReadToEndAsync();
	var names = text.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
	engine.ImportPeople(names);
	return Results.Ok();
});

app.MapDelete("/api/people/{name}", (SettlementEngine engine, string name) =>
{
	return engine.RemovePerson(name) ? Results.Ok() : Results.NotFound();
});

app.MapPost("/api/expenses", (SettlementEngine engine, ExpenseDto dto) =>
{
	engine.AddExpense(dto.Description, dto.Amount, dto.Payer, dto.Participants);
	return Results.Ok();
});

app.MapDelete("/api/expenses/{id:guid}", (SettlementEngine engine, Guid id) =>
{
	return engine.RemoveExpense(id) ? Results.Ok() : Results.NotFound();
});

app.MapPost("/api/expenses/{id:guid}/participants", (SettlementEngine engine, Guid id, UpdateParticipantsDto dto) =>
{
	return engine.UpdateExpenseParticipants(id, dto.Participants) ? Results.Ok() : Results.NotFound();
});

// Removed /api/net since net balances are now shown within the matrix locally

app.MapGet("/api/expenses", (SettlementEngine engine) =>
{
	return Results.Ok(engine.GetExpenses());
});

app.MapGet("/api/totals", (SettlementEngine engine) =>
{
	return Results.Ok(engine.GetTotalsSpent());
});

app.MapGet("/api/settle", (SettlementEngine engine) =>
{
	return Results.Ok(engine.SettleUp());
});

app.MapPost("/api/reset", (SettlementEngine engine) =>
{
	engine.Clear();
	return Results.Ok();
});

app.MapGet("/api/export/excel", (SettlementEngine engine) =>
{
	var totals = engine.GetTotalsSpent();
	var net = engine.GetNetBalances(); // still used internally for transfers and matrix export
	var expenses = engine.GetExpenses();
	var transfers = engine.SettleUp();

	using var wb = new XLWorkbook();

	// People
	var wsPeople = wb.AddWorksheet("People");
	wsPeople.Cell(1, 1).Value = "Name";
	for (int i = 0; i < net.Count; i++)
	{
		wsPeople.Cell(i + 2, 1).Value = net[i].Name;
	}

	// Totals
	var wsTotals = wb.AddWorksheet("TotalsSpent");
	wsTotals.Cell(1, 1).Value = "Name"; wsTotals.Cell(1, 2).Value = "Spent";
	for (int i = 0; i < totals.Count; i++)
	{
		wsTotals.Cell(i + 2, 1).Value = totals[i].Name;
		wsTotals.Cell(i + 2, 2).Value = totals[i].Spent;
	}

// NetBalances worksheet removed (net now visible in matrix export Net column)

	// Expenses
	var wsExp = wb.AddWorksheet("Expenses");
	wsExp.Cell(1, 1).Value = "Description";
	wsExp.Cell(1, 2).Value = "Amount";
	wsExp.Cell(1, 3).Value = "Payer";
	wsExp.Cell(1, 4).Value = "Participants";
	for (int i = 0; i < expenses.Count; i++)
	{
		var e = expenses[i];
		wsExp.Cell(i + 2, 1).Value = e.Description;
		wsExp.Cell(i + 2, 2).Value = e.Amount;
		wsExp.Cell(i + 2, 3).Value = e.Payer;
		wsExp.Cell(i + 2, 4).Value = string.Join(", ", e.Participants);
	}

	// Transfers
	var wsTrans = wb.AddWorksheet("Transfers");
	wsTrans.Cell(1, 1).Value = "From";
	wsTrans.Cell(1, 2).Value = "To";
	wsTrans.Cell(1, 3).Value = "Amount";
	for (int i = 0; i < transfers.Count; i++)
	{
		var t = transfers[i];
		wsTrans.Cell(i + 2, 1).Value = t.From;
		wsTrans.Cell(i + 2, 2).Value = t.To;
		wsTrans.Cell(i + 2, 3).Value = t.Amount;
	}


	// Expense Matrix (people vs expenses)
	var wsMatrix = wb.AddWorksheet("Matrix");
	// Header row: first cell blank then each expense description with index, final Net column
	wsMatrix.Cell(1,1).Value = "Person";
	for (int c = 0; c < expenses.Count; c++)
	{
		var e = expenses[c];
		wsMatrix.Cell(1, c+2).Value = $"{c+1}. {e.Description}";
		wsMatrix.Cell(2, c+2).Value = $"Payer: {e.Payer}";
		wsMatrix.Cell(3, c+2).Value = $"Amt: {e.Amount}";
	}
	wsMatrix.Cell(1, expenses.Count + 2).Value = "Net";
	wsMatrix.Range(1,1,3,1).Merge(); // merge person header vertical
	// People rows start at row 4
	for (int i = 0; i < net.Count; i++)
	{
		var person = net[i].Name;
		var row = i + 4;
		wsMatrix.Cell(row,1).Value = person;
		for (int c = 0; c < expenses.Count; c++)
		{
			var e = expenses[c];
			var col = c + 2;
			decimal value = 0m;
			// Payer gets +amount
			if (string.Equals(e.Payer, person, StringComparison.OrdinalIgnoreCase))
			{
				value = e.Amount;
			}
			else if (e.Participants.Contains(person, StringComparer.OrdinalIgnoreCase))
			{
				// participant share negative (inline allocation copied from engine for determinism)
				var participants = e.Participants;
				var totalAmount = e.Amount;
				var totalCents = (long)(Math.Round(totalAmount, 2, MidpointRounding.AwayFromZero) * 100m);
				var count = participants.Count;
				if (count > 0)
				{
					var basePerPerson = totalCents / count;
					var remainder = totalCents - (basePerPerson * count);
					var baseCentsByPerson = new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
					foreach (var p in participants) baseCentsByPerson[p] = basePerPerson;
					for (int r = 0; r < remainder; r++)
					{
						var p = participants[r % participants.Count];
						baseCentsByPerson[p] += 1;
					}
					decimal share = 0m;
					if (baseCentsByPerson.TryGetValue(person, out var cents)) share = cents / 100m;
					value = -share;
				}
			}
			// non participant cells explicitly 0 (already default)
			wsMatrix.Cell(row, col).Value = value;
		}
		wsMatrix.Cell(row, expenses.Count + 2).Value = net[i].Net;
	}

	// Style matrix (simple): bold headers, freeze panes
	wsMatrix.SheetView.FreezeRows(3);
	wsMatrix.Row(1).Style.Font.Bold = true;
	wsMatrix.Row(2).Style.Font.Italic = true;
	wsMatrix.Row(3).Style.Font.Italic = true;
	wsMatrix.Column(1).Style.Font.Bold = true;

	foreach (var ws in wb.Worksheets)
	{
		ws.Columns().AdjustToContents();
	}

	using var ms = new MemoryStream();
	wb.SaveAs(ms);
	ms.Position = 0;
	var fileName = $"trip-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.xlsx";
	return Results.File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
});

app.Run();

public sealed record PersonDto(string Name);
public sealed record ExpenseDto(string Description, decimal Amount, string Payer, List<string> Participants);
public sealed record UpdateParticipantsDto(List<string> Participants);
