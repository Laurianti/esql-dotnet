// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System.Text.Json;
using Elastic.Esql.Execution;

namespace Elastic.Esql.Tests.Execution;

public class ScalarSelectExecutionTests : EsqlTestBase
{
	private static EsqlQueryable<T> CreateExecutableQuery<T>(CapturingQueryExecutor executor) =>
		new(new EsqlQueryProvider(
			new JsonSerializerOptions
			{
				TypeInfoResolver = EsqlTestMappingContext.Default,
				PropertyNamingPolicy = JsonNamingPolicy.CamelCase
			},
			executor
		));

	[Test]
	public async Task ToListAsync_ComputedDouble_ReadsTheResult()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"result","type":"double"}],"values":[[8.1],[3.0]]}"""
		};

		var result = await CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration * 3)
			.AsEsqlQueryable()
			.ToListAsync();

		_ = result.Should().Equal(8.1, 3.0);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| EVAL result = (duration * 3.0)
			| KEEP result
			""".NativeLineEndings());
	}

	[Test]
	public async Task ToListAsync_ComputedString_ReadsTheResult()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"result","type":"keyword"}],"values":[["A"],["B"]]}"""
		};

		var result = await CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Message.ToUpperInvariant())
			.AsEsqlQueryable()
			.ToListAsync();

		_ = result.Should().Equal("A", "B");
	}

	[Test]
	public async Task ToListAsync_ComputedBoolean_ReadsTheResult()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"result","type":"boolean"}],"values":[[true],[false]]}"""
		};

		var result = await CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.StatusCode >= 500)
			.AsEsqlQueryable()
			.ToListAsync();

		_ = result.Should().Equal(true, false);
	}

	[Test]
	public async Task ToListAsync_Field_ReadsTheField()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"message","type":"keyword"}],"values":[["a"],["b"]]}"""
		};

		var result = await CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Message)
			.AsEsqlQueryable()
			.ToListAsync();

		_ = result.Should().Equal("a", "b");
	}

	[Test]
	public async Task ToListAsync_ComputedWithMetadata_ReadsTheResult()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"result","type":"double"}],"values":[[8.1]]}"""
		};

		var result = await CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*", MetadataField.Id)
			.Select(l => l.Duration * 3)
			.AsEsqlQueryable()
			.ToListAsync();

		_ = result.Should().Equal(8.1);
	}

	[Test]
	public void Max_AfterComputed_AggregatesTheResult()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"max","type":"double"}],"values":[[9.0]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration * 3)
			.Max();

		_ = result.Should().Be(9.0);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| EVAL result = (duration * 3.0)
			| KEEP result
			| STATS max = MAX(result)
			""".NativeLineEndings());
	}

	[Test]
	public void Sum_AfterComputed_AggregatesTheResult()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"sum","type":"double"}],"values":[[11.1]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration * 3)
			.Sum();

		_ = result.Should().Be(11.1);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| EVAL result = (duration * 3.0)
			| KEEP result
			| STATS sum = SUM(result)
			""".NativeLineEndings());
	}

	[Test]
	public void Max_AfterField_AggregatesTheField()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"max","type":"double"}],"values":[[3.0]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration)
			.Max();

		_ = result.Should().Be(3.0);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| KEEP duration
			| STATS max = MAX(duration)
			""".NativeLineEndings());
	}

	[Test]
	public void Count_AfterComputed_CountsTheRows()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"count","type":"long"}],"values":[[2]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration * 3)
			.Count();

		_ = result.Should().Be(2);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| EVAL result = (duration * 3.0)
			| KEEP result
			| STATS count = COUNT(*)
			""".NativeLineEndings());
	}

	[Test]
	public void First_AfterComputed_ReadsTheFirstResult()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"result","type":"double"}],"values":[[8.1]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration * 3)
			.First();

		_ = result.Should().Be(8.1);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| EVAL result = (duration * 3.0)
			| KEEP result
			| LIMIT 1
			""".NativeLineEndings());
	}

	[Test]
	public void Min_AfterComputed_AggregatesTheResult()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"min","type":"double"}],"values":[[3.0]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration * 3)
			.Min();

		_ = result.Should().Be(3.0);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| EVAL result = (duration * 3.0)
			| KEEP result
			| STATS min = MIN(result)
			""".NativeLineEndings());
	}

	[Test]
	public void Average_AfterComputed_AggregatesTheResult()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"avg","type":"double"}],"values":[[5.5]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration * 3)
			.Average();

		_ = result.Should().Be(5.5);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| EVAL result = (duration * 3.0)
			| KEEP result
			| STATS avg = AVG(result)
			""".NativeLineEndings());
	}

	[Test]
	public void Count_WithPredicateAfterComputed_FiltersOnTheResult()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"count","type":"long"}],"values":[[1]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration * 3)
			.Count(x => x > 5);

		_ = result.Should().Be(1);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| EVAL result = (duration * 3.0)
			| KEEP result
			| WHERE result > 5.0
			| STATS count = COUNT(*)
			""".NativeLineEndings());
	}

	[Test]
	public void Any_WithPredicateAfterComputed_FiltersOnTheResult()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"result","type":"boolean"}],"values":[[true]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration * 3)
			.Any(x => x > 5);

		_ = result.Should().Be(true);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| EVAL result = (duration * 3.0)
			| KEEP result
			| WHERE result > 5.0
			| STATS result = COUNT(*)
			| EVAL result = result > 0
			""".NativeLineEndings());
	}

	[Test]
	public async Task ToListAsync_IntegerField_ReadsTheField()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"statusCode","type":"integer"}],"values":[[200],[500]]}"""
		};

		var result = await CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.StatusCode)
			.AsEsqlQueryable()
			.ToListAsync();

		_ = result.Should().Equal(200, 500);
	}

	[Test]
	public void First_WithPredicateAfterComputed_FiltersOnTheResult()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"result","type":"double"}],"values":[[8.1]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration * 3)
			.First(x => x > 5);

		_ = result.Should().Be(8.1);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| EVAL result = (duration * 3.0)
			| KEEP result
			| WHERE result > 5.0
			| LIMIT 1
			""".NativeLineEndings());
	}

	[Test]
	public void FirstOrDefault_NoRowsAfterComputed_ReturnsTheDefault()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"result","type":"double"}],"values":[]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration * 3)
			.FirstOrDefault();

		_ = result.Should().Be(0.0);
	}

	[Test]
	public void LongCount_AfterComputed_CountsTheRows()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"count","type":"long"}],"values":[[2]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration * 3)
			.LongCount();

		_ = result.Should().Be(2L);
	}

	[Test]
	public void Sum_AfterIntegerField_AggregatesTheField()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"sum","type":"long"}],"values":[[700]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.StatusCode)
			.Sum();

		_ = result.Should().Be(700);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| KEEP statusCode
			| STATS sum = SUM(statusCode)
			""".NativeLineEndings());
	}

	[Test]
	public async Task FirstAsync_AfterComputed_ReadsTheFirstResult()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"result","type":"double"}],"values":[[8.1]]}"""
		};

		var result = await CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration * 3)
			.AsEsqlQueryable()
			.FirstAsync();

		_ = result.Should().Be(8.1);
	}

	[Test]
	public async Task CountAsync_AfterComputed_CountsTheRows()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"count","type":"long"}],"values":[[2]]}"""
		};

		var result = await CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration * 3)
			.AsEsqlQueryable()
			.CountAsync();

		_ = result.Should().Be(2);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| EVAL result = (duration * 3.0)
			| KEEP result
			| STATS count = COUNT(*)
			""".NativeLineEndings());
	}

	[Test]
	public async Task AnyAsync_AfterComputedAndWhere_FiltersOnTheResult()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"result","type":"boolean"}],"values":[[true]]}"""
		};

		var result = await CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration * 3)
			.Where(x => x > 5)
			.AsEsqlQueryable()
			.AnyAsync();

		_ = result.Should().BeTrue();
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| EVAL result = (duration * 3.0)
			| KEEP result
			| WHERE result > 5.0
			| STATS result = COUNT(*)
			| EVAL result = result > 0
			""".NativeLineEndings());
	}

	[Test]
	public void Sum_AfterDoubleField_AggregatesTheField()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"sum","type":"double"}],"values":[[3.0]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration)
			.Sum();

		_ = result.Should().Be(3.0);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| KEEP duration
			| STATS sum = SUM(duration)
			""".NativeLineEndings());
	}

	[Test]
	public void Average_AfterIntegerField_AggregatesTheField()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"avg","type":"double"}],"values":[[350.0]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.StatusCode)
			.Average();

		_ = result.Should().Be(350.0);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| KEEP statusCode
			| STATS avg = AVG(statusCode)
			""".NativeLineEndings());
	}

	[Test]
	public void Sum_AfterNullableField_AggregatesTheField()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"sum","type":"double"}],"values":[[3.0]]}"""
		};

		var result = CreateExecutableQuery<MetricDocument>(executor)
			.From("metrics")
			.Select(m => m.Value)
			.Sum();

		_ = result.Should().Be(3.0);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM metrics
			| KEEP value
			| STATS sum = SUM(value)
			""".NativeLineEndings());
	}

	[Test]
	public void Average_AfterNullableField_AggregatesTheField()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"avg","type":"double"}],"values":[[3.0]]}"""
		};

		var result = CreateExecutableQuery<MetricDocument>(executor)
			.From("metrics")
			.Select(m => m.Count)
			.Average();

		_ = result.Should().Be(3.0);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM metrics
			| KEEP count
			| STATS avg = AVG(count)
			""".NativeLineEndings());
	}

	[Test]
	public void Average_AfterNullableComputed_AggregatesTheResult()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"avg","type":"double"}],"values":[[3.0]]}"""
		};

		var result = CreateExecutableQuery<MetricDocument>(executor)
			.From("metrics")
			.Select(m => m.Value * 2)
			.Average();

		_ = result.Should().Be(3.0);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM metrics
			| EVAL result = (value * 2)
			| KEEP result
			| STATS avg = AVG(result)
			""".NativeLineEndings());
	}

	[Test]
	public void Max_AfterNullableField_AggregatesTheField()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"max","type":"double"}],"values":[[3.0]]}"""
		};

		var result = CreateExecutableQuery<MetricDocument>(executor)
			.From("metrics")
			.Select(m => m.Value)
			.Max();

		_ = result.Should().Be(3.0);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM metrics
			| KEEP value
			| STATS max = MAX(value)
			""".NativeLineEndings());
	}

	[Test]
	public void Sum_AfterFloatField_AggregatesTheField()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"sum","type":"double"}],"values":[[3.0]]}"""
		};

		var result = CreateExecutableQuery<BookProjection>(executor)
			.From("books")
			.Select(b => b.Score)
			.Sum();

		_ = result.Should().Be(3.0f);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM books
			| KEEP score
			| STATS sum = SUM(score)
			""".NativeLineEndings());
	}

	[Test]
	public void Sum_AfterComputedInteger_AggregatesTheResult()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"sum","type":"long"}],"values":[[3]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.StatusCode * 2)
			.Sum();

		_ = result.Should().Be(3);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| EVAL result = (statusCode * 2)
			| KEEP result
			| STATS sum = SUM(result)
			""".NativeLineEndings());
	}

	[Test]
	public void Min_AfterStringField_AggregatesTheField()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"min","type":"keyword"}],"values":[["a"]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Message)
			.Min();

		_ = result.Should().Be("a");
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| KEEP message
			| STATS min = MIN(message)
			""".NativeLineEndings());
	}

	[Test]
	public void Count_WithPredicateAfterField_FiltersOnTheField()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"count","type":"long"}],"values":[[1]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.StatusCode)
			.Count(s => s > 500);

		_ = result.Should().Be(1);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| KEEP statusCode
			| WHERE statusCode > 500
			| STATS count = COUNT(*)
			""".NativeLineEndings());
	}

	[Test]
	public void Any_AfterComputed_CountsTheRows()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"result","type":"boolean"}],"values":[[true]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration * 3)
			.Any();

		_ = result.Should().Be(true);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| EVAL result = (duration * 3.0)
			| KEEP result
			| STATS result = COUNT(*)
			| EVAL result = result > 0
			""".NativeLineEndings());
	}

	[Test]
	public void First_AfterField_ReadsTheField()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"message","type":"keyword"}],"values":[["a"]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Message)
			.First();

		_ = result.Should().Be("a");
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| KEEP message
			| LIMIT 1
			""".NativeLineEndings());
	}

	[Test]
	public void Single_AfterComputed_ReadsTheOnlyResult()
	{
		var executor = new CapturingQueryExecutor
		{
			ResponseJson = /*lang=json,strict*/ """{"columns":[{"name":"result","type":"double"}],"values":[[8.1]]}"""
		};

		var result = CreateExecutableQuery<LogEntry>(executor)
			.From("logs-*")
			.Select(l => l.Duration * 3)
			.Single();

		_ = result.Should().Be(8.1);
		_ = executor.Calls[0].Esql.Should().Be(
			"""
			FROM logs-*
			| EVAL result = (duration * 3.0)
			| KEEP result
			| LIMIT 2
			""".NativeLineEndings());
	}
}
