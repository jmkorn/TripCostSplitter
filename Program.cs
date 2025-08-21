using TripSplit;

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

app.MapPost("/api/expenses", (SettlementEngine engine, ExpenseDto dto) =>
{
	engine.AddExpense(dto.Description, dto.Amount, dto.Payer, dto.Participants);
	return Results.Ok();
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

app.Run();

public sealed record PersonDto(string Name);
public sealed record ExpenseDto(string Description, decimal Amount, string Payer, List<string> Participants);
