// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

namespace Elastic.Esql.Tests.Translation.WhereClause;

/// <summary>
/// Ordering comparisons between strings, written in LINQ as CompareTo against zero.
/// These are what keyset pagination needs when the tie-breaker is a text field.
/// </summary>
public class StringComparisonTests : EsqlTestBase
{
	[Test]
	public void CompareTo_GreaterThanZero_TranslatesToGreaterThan()
	{
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => l.Message.CompareTo("m") > 0)
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE message > "m"
            """.NativeLineEndings());
	}

	[Test]
	public void CompareTo_LessThanOrEqualZero_TranslatesToLessThanOrEqual()
	{
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => l.Message.CompareTo("m") <= 0)
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE message <= "m"
            """.NativeLineEndings());
	}

	[Test]
	public void StaticCompare_TranslatesToTheSameComparison()
	{
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.Compare(l.Message, "m") > 0)
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE message > "m"
            """.NativeLineEndings());
	}

	[Test]
	public void ZeroOnTheLeft_FlipsTheOperator()
	{
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => 0 < l.Message.CompareTo("m"))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE message > "m"
            """.NativeLineEndings());
	}

	[Test]
	public void AKeysetPredicate_TranslatesAsAWhole()
	{
		// the shape keyset pagination produces when the tie-breaker is a text field
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => l.Duration > 1.5 || (l.Duration == 1.5 && l.Message.CompareTo("m") > 0))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE (duration > 1.5 OR (duration == 1.5 AND message > "m"))
            """.NativeLineEndings());
	}

	[Test]
	public void CompareOrdinal_IsTranslated()
	{
		// the ordering ES|QL applies to a keyword field is ordinal, so this is the form
		// that means exactly what the translation performs
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.CompareOrdinal(l.Message, "m") > 0)
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE message > "m"
            """.NativeLineEndings());
	}

	[Test]
	public void CompareWithOrdinalComparison_IsTranslated()
	{
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.Compare(l.Message, "m", StringComparison.Ordinal) > 0)
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE message > "m"
            """.NativeLineEndings());
	}
}
