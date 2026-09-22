// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using Elastic.Esql.Functions;

namespace Elastic.Esql.Tests.Translation.WhereClause;

/// <summary>
/// Overloads and values the translation cannot honour. Each one has to be refused
/// rather than translated into a predicate that quietly means something else.
/// </summary>
public class UnsupportedOverloadTests : EsqlTestBase
{
	[Test]
	public void Where_CompareWithCultureSensitiveComparison_ThrowsNotSupported()
	{
		// an ordinal comparison is what ES|QL performs; a culture-sensitive one asks
		// for a different ordering and is refused rather than answered with this one
		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.Compare(l.Message, "m", StringComparison.CurrentCulture) > 0);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*StringComparison.Ordinal*");
	}

	[Test]
	public void Where_CompareToAnObject_ThrowsNotSupported()
	{
		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => l.Message.CompareTo((object)"m") > 0);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*CompareOrdinal*");
	}

	[Test]
	public void Where_CompareTo_ThrowsNotSupported()
	{
		// CompareTo orders by the current culture, where "B" sorts after "a"; a keyword
		// field is ordered by its UTF-8 bytes, which is what CompareOrdinal asks for
		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => l.Message.CompareTo("m") > 0);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*CompareOrdinal*");
	}

	[Test]
	public void Where_TwoArgumentCompare_ThrowsNotSupported()
	{
		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.Compare(l.Message, "m") > 0);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*CompareOrdinal*");
	}

	[Test]
	public void Where_CompareOrdinalOverARange_ThrowsNotSupported()
	{
		// ordinal, but over a substring of each operand: not the ordering of the field
		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.CompareOrdinal(l.Message, 0, "m", 0, 1) > 0);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*overload*");
	}

	[Test]
	public void Where_CompareOrdinalOutsideAComparisonAgainstZero_ThrowsNotSupported()
	{
		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.CompareOrdinal(l.Message, "m") == 1);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*against zero*");
	}

	[Test]
	public void Where_CompareOrdinalAgainstNull_ThrowsNotSupported()
	{
		// .NET orders a non-null string above null; an ES|QL comparison against null
		// does not reproduce that
		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.CompareOrdinal(l.Message, (string?)null) > 0);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*IS NULL*");
	}

	[Test]
	public void Where_CompareOrdinalAgainstCapturedNull_ThrowsNotSupported()
	{
		var missing = (string?)null;

		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.CompareOrdinal(l.Message, missing) > 0);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*IS NULL*");
	}

	[Test]
	public void Where_CompareWithOrdinalIgnoreCase_ThrowsNotSupported()
	{
		// keyword ordering is case-sensitive: "a" sorts after "B" there, and before it
		// under OrdinalIgnoreCase, so the two disagree
		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.Compare(l.Message, "m", StringComparison.OrdinalIgnoreCase) > 0);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*StringComparison.Ordinal*");
	}

	[Test]
	public void Where_CompareBetweenTwoNullableFields_ThrowsNotSupported()
	{
		// two absent values have no single ES|QL form for their ordering
		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.Compare(l.ClientIp, l.ServerName, StringComparison.Ordinal) < 0);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*two fields*");
	}

	[Test]
	public void Where_CompareWithIgnoreCaseFlag_NamesTheComparisonMode()
	{
		// the shape is the supported one, only the overload is not: the message says so
		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.Compare(l.Message, "m", true) > 0);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*overload*");
	}

	[Test]
	public void Where_CompareAgainstNull_NamesTheNullOperand()
	{
		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.Compare(l.Message, null, StringComparison.Ordinal) > 0);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*IS NULL*");
	}

	[Test]
	public void Where_CompareBetweenAFieldExpressionAndAField_NamesTheTwoFields()
	{
		// both sides read a field, one through a function: there is no value to look at,
		// so the refusal is the two-fields one rather than the unreadable-value one
		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.CompareOrdinal(EsqlFunctions.Trim(l.Message), l.ClientIp!) > 0);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*between two fields*");
	}

	[Test]
	public void Where_CompareAgainstAComputedValue_NamesTheUnreadableValue()
	{
		// a function of a captured value is a value all the same, whatever its shape, so
		// it is refused for what it is rather than as a second field
		var prefix = "m";

		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => string.CompareOrdinal(l.Message, prefix + "x") > 0);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*cannot read*");
	}

	[Test]
	public void Where_CompareOrdinalOnAConvertedPropertyBehindAMultiField_ThrowsNotSupported()
	{
		// the sub-field holds what the converter writes just as the field does, so the
		// refusal has to look through the call rather than only at a bare member
		var query = CreateQuery<PrefixedCodeDocument>()
			.From("docs")
			.Where(d => string.CompareOrdinal(d.Code.MultiField("keyword"), "42") > 0);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*JsonConverter*");
	}
}
