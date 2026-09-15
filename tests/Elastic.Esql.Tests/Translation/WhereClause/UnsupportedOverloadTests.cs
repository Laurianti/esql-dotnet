// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

namespace Elastic.Esql.Tests.Translation.WhereClause;

/// <summary>
/// Overloads and values the translation cannot honour. Each one has to be refused
/// rather than translated into a predicate that quietly means something else.
/// </summary>
public class UnsupportedOverloadTests : EsqlTestBase
{
	[Test]
	public void CompareWithAStringComparison_IsRefused()
	{
		// dropping the comparison mode would silently make the predicate case-sensitive
		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.Compare(l.Message, "m", StringComparison.OrdinalIgnoreCase) > 0);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}

	[Test]
	public void CompareToAnObject_IsRefused()
	{
		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => l.Message.CompareTo((object)"m") > 0);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}

	[Test]
	public void ContainsWithAComparer_IsRefused()
	{
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Contains("x", StringComparer.OrdinalIgnoreCase));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}

	[Test]
	public void AnElementComparedToNull_IsRefused()
	{
		// a multi-value field stores no null element, and MATCH(field, null) is not valid
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t == null));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}

	[Test]
	public void AllElementsComparedToNull_IsRefused()
	{
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.All(t => t != null));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}

	[Test]
	public void ContainsNull_IsRefused()
	{
		var missing = (string?)null;

		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Contains(missing!));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}

	[Test]
	public void CompareToNull_IsRefused()
	{
		// .NET orders a non-null string above null; an ES|QL comparison against null
		// does not reproduce that
		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => l.Message.CompareTo((string?)null) > 0);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}

	[Test]
	public void CompareToACapturedNull_IsRefused()
	{
		var missing = (string?)null;

		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => l.Message.CompareTo(missing) > 0);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}

	[Test]
	public void AConstantCollectionContainsWithAComparer_IsRefused()
	{
		var wanted = new[] { "a", "b" };

		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => wanted.Contains(t, StringComparer.OrdinalIgnoreCase)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}
}
