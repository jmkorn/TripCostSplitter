using TripSplit;
using ClosedXML.Excel;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<SettlementEngine>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/people", (SettlementEngine engine) =>
{
	return Results.Ok(engine.GetNetBalances().Select(x => x.Name).ToList());
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

app.MapGet("/api/net", (SettlementEngine engine) =>
{
	return Results.Ok(engine.GetNetBalances());
});

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
	var net = engine.GetNetBalances();
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

	// Net Balances
	var wsNet = wb.AddWorksheet("NetBalances");
	wsNet.Cell(1, 1).Value = "Name"; wsNet.Cell(1, 2).Value = "Net";
	for (int i = 0; i < net.Count; i++)
	{
		wsNet.Cell(i + 2, 1).Value = net[i].Name;
		wsNet.Cell(i + 2, 2).Value = net[i].Net;
	}

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
