// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

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
	public void ANullGuardOnTheDocumentItself_IsAConstant()
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
            | WHERE (TRUE AND clientIp IS NOT NULL)
            """.NativeLineEndings());
	}

	[Test]
	public void ADocumentComparedToNull_IsFalse()
	{
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Where(l => l == null)
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | WHERE FALSE
            """.NativeLineEndings());
	}
}
