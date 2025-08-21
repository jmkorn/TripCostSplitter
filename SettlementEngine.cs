using System;
using System.Collections.Generic;
using System.Linq;

namespace TripSplit
{
	public sealed class SettlementEngine
	{
		public sealed record Transfer(string From, string To, decimal Amount);
		private readonly Dictionary<string, int> _nameToIndex = new(StringComparer.OrdinalIgnoreCase);
		private readonly List<string> _names = new();
		private readonly List<decimal> _netBalances = new();

		public void Clear()
		{
			_nameToIndex.Clear();
			_names.Clear();
			_netBalances.Clear();
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
		}

		public IReadOnlyList<Transfer> SettleUp()
		{
			var nameAmountPairs = _names.Select((n, i) => (Name: n, Amount: _netBalances[i]));
			var creditorPairs = nameAmountPairs.Where(x => x.Amount > 0m);
			var orderedCreditors = creditorPairs.OrderByDescending(x => x.Amount);
			var creditors = new Queue<(string Name, decimal Amount)>(orderedCreditors);
			var nameAndBalances = _names.Select((n, i) => (Name: n, Amount: _netBalances[i]));
			var negativeBalances = nameAndBalances.Where(x => x.Amount < 0m);
			var debtorsList = negativeBalances
				.Select(x => (x.Name, Amount: -x.Amount))
				.OrderByDescending(x => x.Amount);
			var debtors = new Queue<(string Name, decimal Amount)>(debtorsList);
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

		public IReadOnlyList<(string Name, decimal Net)> GetNetBalances()
		{
			return _names.Select((n, i) => (n, Math.Round(_netBalances[i], 2, MidpointRounding.AwayFromZero))).ToList();
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
			var orderedParticipants = participants
				.Select(p => new { Person = p, Order = participantOrder[p] })
				.OrderBy(x => x.Order)
				.ToList();
			for (int i = 0; i < remainder; i++)
			{
				var person = orderedParticipants[i % orderedParticipants.Count].Person;
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
	}
}


