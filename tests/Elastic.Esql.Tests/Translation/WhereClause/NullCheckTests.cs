// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System.Linq.Expressions;
using Elastic.Esql.Functions;

namespace Elastic.Esql.Tests.Translation.WhereClause;

public class NullCheckTests : EsqlTestBase
{
	[Test]
	public void Where_EqualsNull_GeneratesComparison()
	{
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => l.ClientIp == null)
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE clientIp IS NULL
            """.NativeLineEndings());
	}

	[Test]
	public void Where_NotEqualsNull_GeneratesIsNotNull()
	{
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => l.ClientIp != null)
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE clientIp IS NOT NULL
            """.NativeLineEndings());
	}

	[Test]
	public void Where_NullGuardOnTheDocument_FoldsToAConstant()
	{
		// generated predicates guard the root against null; a document never is,
		// and there is no field name to put in front of IS NOT NULL
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => l != null && l.ClientIp != null)
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE (true AND clientIp IS NOT NULL)
            """.NativeLineEndings());
	}

	[Test]
	public void Where_DocumentComparedToNull_FoldsToFalse()
	{
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => l == null)
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE false
            """.NativeLineEndings());
	}


	[Test]
	public void Where_AfterKeep_TheDocumentGuardStillFolds()
	{
		// Keep narrows the columns but the rows are still documents, so the guard on the
		// document parameter still folds to a constant
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Keep("message")
			.Where(l => l != null)
			.ToString();

		_ = esql.Should().Contain("WHERE true");
	}

	[Test]
	public void Where_ProjectedRowInsideAForkBranch_StaysProjected()
	{
		// the branch starts a fresh context: the parent's projection has to carry over, or
		// the guard on the projected row folds to FALSE and empties the branch
		var query = CreateQuery<TreeNode>()
			.From("nodes")
			.Select(n => n.Child)
			.Fork(b => b.Where(n => n == null), b => b.Take(1));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*projected value*");
	}

	[Test]
	public void Where_AfterAnIdentitySelect_TheDocumentGuardStillFolds()
	{
		// Select(l => l) emits nothing and hands the row back as it is, so the guard on
		// the document parameter is still a constant afterwards
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Select(l => l)
			.Where(l => l != null)
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE true
            """.NativeLineEndings());
	}

	[Test]
	public void Where_NullGuardBehindACast_StillFolds()
	{
		// a hand-built tree converts the row to object so the operands of the comparison
		// have matching types; the guard is the same guard
		var parameter = Expression.Parameter(typeof(LogEntry), "l");
		var guard = Expression.Lambda<Func<LogEntry, bool>>(
			Expression.NotEqual(
				Expression.Convert(parameter, typeof(object)),
				Expression.Constant(null, typeof(object))),
			parameter);

		var esql = CreateQuery<LogEntry>().From("logs-*").Where(guard).ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE true
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AfterAProjectingForkBranch_TheRowIsProjected()
	{
		// the branch projects, so the rows that follow the Fork are projected too and the
		// guard is refused rather than folded into a constant that would drop every row
		var query = CreateQuery<TreeNode>()
			.From("nodes")
			.Fork(b => b.Select(n => n.Child).Take(1), b => b.Take(1))
			.Where(n => n == null);

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*projected value*");
	}

	[Test]
	public void Where_EsqlFunctionsIsNotNullOnTheRow_ThrowsNotSupported()
	{
		// the marker takes the field to test; the row has no field name of its own, and
		// emitting it leaves the operator with nothing in front of it
		var query = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => EsqlFunctions.IsNotNull(l));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*row has no field name*");
	}
}
