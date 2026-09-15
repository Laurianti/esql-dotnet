// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

namespace Elastic.Esql.Tests.Translation.WhereClause;

/// <summary>
/// Ordering comparisons between strings, written in LINQ as CompareOrdinal against zero.
/// These are what keyset pagination needs when the tie-breaker is a text field.
/// </summary>
public class StringComparisonTests : EsqlTestBase
{
	[Test]
	public void CompareOrdinal_GreaterThanZero_TranslatesToGreaterThan()
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
	public void CompareOrdinal_LessThanOrEqualZero_TranslatesToLessThanOrEqual()
	{
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.CompareOrdinal(l.Message, "m") <= 0)
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE message <= "m"
            """.NativeLineEndings());
	}

	[Test]
	public void CompareWithOrdinalComparison_TranslatesToTheSameComparison()
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

	[Test]
	public void ZeroOnTheLeft_FlipsTheOperator()
	{
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => 0 < string.CompareOrdinal(l.Message, "m"))
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
			.Where(l => l.Duration > 1.5 || (l.Duration == 1.5 && string.CompareOrdinal(l.Message, "m") > 0))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE (duration > 1.5 OR (duration == 1.5 AND message > "m"))
            """.NativeLineEndings());
	}

	[Test]
	public void StaticCompareOverANullableField_SpellsOutTheMissingValue()
	{
		// string.Compare orders null before everything, where a comparison against a
		// missing field is null in ES|QL and the row would be dropped
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.Compare(l.ClientIp, "m", StringComparison.Ordinal) < 0)
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE (clientIp IS NULL OR clientIp < "m")
            """.NativeLineEndings());
	}

	[Test]
	public void StaticCompareAboveANullableField_ExcludesTheMissingValue()
	{
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.Compare(l.ClientIp, "m", StringComparison.Ordinal) > 0)
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE (clientIp IS NOT NULL AND clientIp > "m")
            """.NativeLineEndings());
	}

	[Test]
	public void StaticCompareWithTheNullableFieldOnTheRight_GuardsTheOtherWay()
	{
		// null sorts first, so "m" compared against a missing value is above it: the
		// row is out for "<" and in for ">", the mirror of the field on the left
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.Compare("m", l.ClientIp, StringComparison.Ordinal) < 0)
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE (clientIp IS NOT NULL AND "m" < clientIp)
            """.NativeLineEndings());
	}

	[Test]
	public void StaticCompareAboveTheNullableFieldOnTheRight_KeepsTheMissingValue()
	{
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.Compare("m", l.ClientIp, StringComparison.Ordinal) > 0)
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE (clientIp IS NULL OR "m" > clientIp)
            """.NativeLineEndings());
	}

	[Test]
	public void CompareOrdinalWithANullableSecondField_GuardsThatField()
	{
		// string.CompareOrdinal("m", null) is positive in .NET: the first operand is
		// above a missing second one, so the missing value stays in for ">"
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.CompareOrdinal(l.Message, l.ClientIp) > 0)
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE (clientIp IS NULL OR message > clientIp)
            """.NativeLineEndings());
	}

	[Test]
	public void ANullableFieldDeclaredThroughTheTypeContext_IsStillGuarded()
	{
		// every member of OptionalDocument is nullable, so the compiler records that once
		// on the type and not on the member: the guard has to be found there too
		var esql = CreateQuery<OptionalDocument>()
			.From("docs")
			.Where(d => string.Compare(d.ClientIp, "m", StringComparison.Ordinal) < 0)
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM docs
            | WHERE (clientIp IS NULL OR clientIp < "m")
            """.NativeLineEndings());
	}
}
