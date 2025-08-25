using System;
using System.Collections.Generic;
using System.Linq;

namespace TripSplit
{
	public sealed class SettlementEngine
	{
		public sealed record Transfer(string From, string To, decimal Amount);
		public sealed record Expense(Guid Id, string Description, decimal Amount, string Payer, IReadOnlyList<string> Participants);
		public sealed record NetBalance(string Name, decimal Net);
		public sealed record TotalSpent(string Name, decimal Spent);
		private readonly Dictionary<string, int> _nameToIndex = new(StringComparer.OrdinalIgnoreCase);
		private readonly List<string> _names = new();
		private readonly List<decimal> _netBalances = new();
		private readonly List<Expense> _expenses = new();

		public IReadOnlyList<string> GetPeople() => _names.ToList();

		public void Clear()
		{
			_nameToIndex.Clear();
			_names.Clear();
			_netBalances.Clear();
			_expenses.Clear();
		}

		public void AddPerson(string name)
		{
			if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name cannot be null or whitespace.", nameof(name));
			if (_nameToIndex.ContainsKey(name)) return;
			_nameToIndex[name] = _names.Count;
			_names.Add(name);
			_netBalances.Add(0m);
		}

		public void AddExpense(string description, decimal amount, string payer, IEnumerable<string> participants)
		{
			if (amount <= 0m) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");
			if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("Description required", nameof(description));
			if (!_nameToIndex.ContainsKey(payer)) throw new ArgumentException($"Unknown payer {payer}. Add them first.");
			var participantList = participants?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
			if (participantList.Count == 0) throw new ArgumentException("At least one participant is required.", nameof(participants));
			foreach (var person in participantList.Concat(new[] { payer }))
			{
				if (!_nameToIndex.ContainsKey(person)) throw new ArgumentException($"Unknown person {person}. Add them first.");
			}
			// Ensure payer is included among participants
			if (!participantList.Contains(payer, StringComparer.OrdinalIgnoreCase))
			{
				participantList.Add(payer);
			}

			var shareByPerson = AllocateShares(amount, participantList);
			_netBalances[_nameToIndex[payer]] += amount;
			foreach (var kvp in shareByPerson)
			{
				_netBalances[_nameToIndex[kvp.Key]] -= kvp.Value;
			}
			_expenses.Add(new Expense(Guid.NewGuid(), description, Math.Round(amount, 2, MidpointRounding.AwayFromZero), payer, participantList.ToList()));
		}

		public void ImportPeople(IEnumerable<string> names)
		{
			if (names == null) return;
			foreach (var n in names.Select(n => n?.Trim()).Where(n => !string.IsNullOrWhiteSpace(n)))
			{
				AddPerson(n!);
			}
		}

		public bool RemoveExpense(Guid id)
		{
			var idx = _expenses.FindIndex(e => e.Id == id);
			if (idx < 0) return false;
			_expenses.RemoveAt(idx);
			RecalculateNetBalances();
			return true;
		}

		public bool RemovePerson(string name)
		{
			if (!_nameToIndex.ContainsKey(name)) return false;
			// Remove expenses referencing this person
			_expenses.RemoveAll(e => e.Payer.Equals(name, StringComparison.OrdinalIgnoreCase) || e.Participants.Contains(name, StringComparer.OrdinalIgnoreCase));
			// Remove person from dictionaries/lists
			var index = _nameToIndex[name];
			_nameToIndex.Remove(name);
			_names.RemoveAt(index);
			_netBalances.RemoveAt(index);
			// Rebuild index map
			for (int i = 0; i < _names.Count; i++) _nameToIndex[_names[i]] = i;
			RecalculateNetBalances();
			return true;
		}

		private void RecalculateNetBalances()
		{
			for (int i = 0; i < _netBalances.Count; i++) _netBalances[i] = 0m;
			foreach (var exp in _expenses)
			{
				var shareByPerson = AllocateShares(exp.Amount, exp.Participants);
				_netBalances[_nameToIndex[exp.Payer]] += exp.Amount;
				foreach (var kvp in shareByPerson)
				{
					_netBalances[_nameToIndex[kvp.Key]] -= kvp.Value;
				}
			}
		}

		public IReadOnlyList<Transfer> SettleUp()
		{
			var creditors = new Queue<(string Name, decimal Amount)>(_names.Select((n, i) => (Name: n, Amount: _netBalances[i])).Where(x => x.Amount > 0m).OrderByDescending(x => x.Amount));
			var debtors = new Queue<(string Name, decimal Amount)>(_names.Select((n, i) => (Name: n, Amount: _netBalances[i])).Where(x => x.Amount < 0m).Select(x => (x.Name, Amount: -x.Amount)).OrderByDescending(x => x.Amount));
			var transfers = new List<Transfer>();
			while (creditors.Count > 0 && debtors.Count > 0)
			{
				var (creditorName, creditorAmt) = creditors.Dequeue();
				var (debtorName, debtorAmt) = debtors.Dequeue();
				var pay = Min(creditorAmt, debtorAmt);
				pay = Math.Round(pay, 2, MidpointRounding.AwayFromZero);
				if (pay > 0m) transfers.Add(new Transfer(debtorName, creditorName, pay));
				var remainingCreditor = creditorAmt - pay;
				var remainingDebtor = debtorAmt - pay;
				if (remainingCreditor > 0m) creditors.Enqueue((creditorName, remainingCreditor));
				if (remainingDebtor > 0m) debtors.Enqueue((debtorName, remainingDebtor));
			}
			return transfers;
		}

		public IReadOnlyList<NetBalance> GetNetBalances() => _names
			.Select((n, i) => new NetBalance(n, Math.Round(_netBalances[i], 2, MidpointRounding.AwayFromZero)))
			.ToList();

		public IReadOnlyList<Expense> GetExpenses() => _expenses.ToList();

		public IReadOnlyList<TotalSpent> GetTotalsSpent()
		{
			var byPayer = _expenses
				.GroupBy(e => e.Payer, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.Sum(x => x.Amount), StringComparer.OrdinalIgnoreCase);
			return _names
				.Select(n => new TotalSpent(n, Math.Round(byPayer.TryGetValue(n, out var amt) ? amt : 0m, 2, MidpointRounding.AwayFromZero)))
				.ToList();
		}

		public bool UpdateExpenseParticipants(Guid id, IEnumerable<string> participants)
		{
			var idx = _expenses.FindIndex(e => e.Id == id);
			if (idx < 0) return false;
			var exp = _expenses[idx];
			var distinct = participants?.Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();
			if (!distinct.Contains(exp.Payer, StringComparer.OrdinalIgnoreCase))
			{
				distinct.Add(exp.Payer);
			}
			// validate all participants exist
			foreach (var p in distinct)
			{
				if (!_nameToIndex.ContainsKey(p)) throw new ArgumentException($"Unknown person {p}. Add them first.");
			}
			// Replace record
			_expenses[idx] = exp with { Participants = distinct };
			RecalculateNetBalances();
			return true;
		}

		private static decimal Min(decimal a, decimal b) => a < b ? a : b;

		private static Dictionary<string, decimal> AllocateShares(decimal totalAmount, IReadOnlyList<string> participants)
		{
			var totalCents = DecimalToCents(totalAmount);
			var participantOrder = participants.Select((person, index) => new { person, index }).ToDictionary(x => x.person, x => x.index, StringComparer.OrdinalIgnoreCase);
			var count = participants.Count;
			if (count <= 0) return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
			var basePerPerson = totalCents / count;
			var remainder = totalCents - (basePerPerson * count);
			var baseCentsByPerson = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
			foreach (var p in participants)
			{
				baseCentsByPerson[p] = basePerPerson;
			}
			// Distribute remaining cents deterministically by participant order
			var ordered = participants
				.Select(p => new { Person = p, Order = participantOrder[p] })
				.OrderBy(x => x.Order)
				.ToList();
			for (int i = 0; i < remainder; i++)
			{
				var person = ordered[i % ordered.Count].Person;
				baseCentsByPerson[person] += 1;
			}
			var shares = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
			foreach (var p in participants)
			{
				shares[p] = CentsToDecimal(baseCentsByPerson[p]);
			}
			var sumShares = shares.Values.Sum();
			if (sumShares != totalAmount)
			{
				var adjust = totalAmount - sumShares;
				var first = participants[0];
				shares[first] = shares[first] + adjust;
			}
			return shares;
		}

		private static long DecimalToCents(decimal amount)
		{
			var rounded = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
			return (long)(rounded * 100m);
		}

		private static decimal CentsToDecimal(long cents) => cents / 100m;

		/// <summary>
		/// Builds a deterministic, compact plain‑text prompt describing the current settlement state
		/// for the LLM explanation. Contains: participants, each expense with allocated shares,
		/// net balances, and suggested transfers produced by the minimal settlement algorithm.
		/// Keeps wording stable so cached / repeated calls produce consistent model behavior.
		/// </summary>
		public string BuildExplanationPrompt()
		{
			var sb = new System.Text.StringBuilder();
			var people = GetPeople();
			var expenses = GetExpenses();
			var nets = GetNetBalances();
			var transfers = SettleUp();

			sb.AppendLine("Trip cost settlement context");
			sb.AppendLine("Participants (alphabetical): " + string.Join(", ", people.OrderBy(p => p)));
			sb.AppendLine();

			if (expenses.Count == 0)
			{
				sb.AppendLine("No expenses have been recorded. Everyone is settled.");
				// Still include empty sections for structural stability
				sb.AppendLine();
				sb.AppendLine("Net balances: (all 0.00)");
				foreach (var p in people.OrderBy(p => p)) sb.AppendLine($"- {p}: 0.00");
				sb.AppendLine("Suggested transfers: none");
				sb.AppendLine();
				sb.AppendLine("Instruction: Provide a brief confirmation that no settlement actions are needed.");
				return sb.ToString();
			}

			sb.AppendLine("Expenses (each shows equal-share allocation in USD):");
			for (int i = 0; i < expenses.Count; i++)
			{
				var e = expenses[i];
				var shares = AllocateShares(e.Amount, e.Participants);
				var shareParts = e.Participants
					.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
					.Select(p => $"{p}:{shares[p]:0.00}");
				sb.AppendLine($"{i+1}. {e.Description} | Amount:{e.Amount:0.00} | Payer:{e.Payer} | Participants:{string.Join(",", e.Participants)} | Shares:{string.Join("; ", shareParts)}");
			}
			sb.AppendLine();

			sb.AppendLine("Net balances (positive means the person is owed money; negative means they owe):");
			foreach (var n in nets.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
			{
				var sign = n.Net >= 0 ? "+" : ""; // show + for positives to disambiguate
				sb.AppendLine($"- {n.Name}: {sign}{n.Net:0.00}");
			}
			sb.AppendLine();

			sb.AppendLine("Suggested settlement transfers (minimal set):");
			if (transfers.Count == 0)
			{
				sb.AppendLine("(none – everyone already even)");
			}
			else
			{
				foreach (var t in transfers)
				{
					sb.AppendLine($"- {t.From} -> {t.To}: {t.Amount:0.00}");
				}
			}
			sb.AppendLine();

			sb.AppendLine("Instruction to model: In under 250 words, explain concisely why each participant owes or is owed these amounts. Avoid repeating the raw tables verbatim; focus on rationale and fairness. Keep explanations simple and a tone like you are explaining it to middle schoolers.");
			return sb.ToString();
		}
	}
}


