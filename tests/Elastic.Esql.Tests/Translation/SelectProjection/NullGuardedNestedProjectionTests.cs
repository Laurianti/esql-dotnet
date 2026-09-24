// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System.Linq.Expressions;

namespace Elastic.Esql.Tests.Translation.SelectProjection;

/// <summary>
/// Projections of the shape a GraphQL layer emits for a nested selection:
/// <c>param == null ? null : new Child { Field = param.Child.Field }</c>.
/// Without the null guard this already worked; with it, it did not. The guard
/// only stands for the branch when the branch reads through the guarded path.
/// </summary>
public class NullGuardedNestedProjectionTests : EsqlTestBase
{
	[Test]
	public void Select_NullGuardedNestedInit_ProjectsTheInnerField()
	{
		var esql = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument
			{
				Host = l.Host == null ? null : new NestedSelectionHost { Name = l.Host.Name }
			})
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | KEEP host.name
            """.NativeLineEndings());
	}

	[Test]
	public void Select_GuardedChildOfAGuardedChild_IsUnwrappedTwice()
	{
		// the shape a selection two levels deep takes: the inner guard is null whenever
		// its path is, and its path goes through the outer one
		var esql = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument
			{
				Host = l.Host == null
					? null
					: new NestedSelectionHost
					{
						Name = l.Host.Name,
						Geo = l.Host.Geo == null ? null : new NestedSelectionGeo { City = l.Host.Geo.City }
					}
			})
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | KEEP host.name, host.geo.city
            """.NativeLineEndings());
	}

	[Test]
	public void Select_InnerGuardOnAnUnrelatedPath_ThrowsNotSupported()
	{
		// the inner child is null when Agent is missing, not when Host is: the outer
		// guard cannot be folded away
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument
			{
				Host = l.Host == null
					? null
					: new NestedSelectionHost
					{
						Geo = l.Agent == null ? null : new NestedSelectionGeo { City = l.Host!.Geo!.City }
					}
			});

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*reads through*");
	}

	[Test]
	public void Select_GuardIntoANonNullableMember_ThrowsNotSupported()
	{
		// with the guard dropped, a missing parent comes back as the member's default,
		// which for a member with an initializer is an object rather than the null the
		// guard produces: there is no way to carry that null, so the shape is refused
		var query = CreateQuery<EagerNestedDocument>()
			.From("logs-*")
			.Select(l => new EagerNestedDocument
			{
				Host = l.Host == null ? null! : new NestedSelectionHost { Name = l.Host!.Name }
			});

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*not declared nullable*");
	}

	[Test]
	public void Select_GuardIntoANonNullableConstructorParameter_ThrowsNotSupported()
	{
		// the constructor's parameter stands for the member: declared non-nullable, it
		// cannot hold the null the guard produces
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new EagerHostRecord(l.Host == null ? null! : new NestedSelectionHost { Name = l.Host!.Name }));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*not declared nullable*");
	}

	[Test]
	public void Select_GuardIntoANullableConstructorParameter_IsUnwrapped()
	{
		var esql = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new LazyHostRecord(l.Host == null ? null : new NestedSelectionHost { Name = l.Host.Name }))
			.ToString();

		_ = esql.Should().Contain("host.name");
	}

	[Test]
	public void Select_PlainNestedInit_StillProjects()
	{
		var esql = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument
			{
				Host = new NestedSelectionHost { Name = l.Host!.Name }
			})
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | KEEP host.name
            """.NativeLineEndings());
	}

	[Test]
	public void Select_GuardOverAnUnrelatedBranch_ThrowsNotSupported()
	{
		// the guard tests Host, the branch reads Message: the two are unrelated, so the
		// guard cannot be folded away, and the general conditional fallback renders the
		// test with the C# operator rather than an ES|QL IS NULL
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new { Value = l.Host == null ? null : l.Message });

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*reads through*");
	}

	[Test]
	public void Select_GuardOverAFunctionOfTheGuardedPath_IsUnwrapped()
	{
		// TRIM of a missing value is null, as every scalar function is over a null input,
		// so the branch is null exactly when the guard says so and the guard can go
		var esql = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument
			{
				Host = l.Host == null ? null : new NestedSelectionHost { Name = EsqlFunctions.Trim(l.Host.Name) }
			})
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | EVAL host.name = TRIM(host.name)
            | KEEP host.name
            """.NativeLineEndings());
	}

	[Test]
	public void Select_GuardOverAParamsFunctionOfTheGuardedPath_IsUnwrapped()
	{
		// the values of a params call sit in an array of their own; CONCAT is null over a
		// null input like any other function, so the guard can go
		var esql = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument
			{
				Host = l.Host == null ? null : new NestedSelectionHost { Name = EsqlFunctions.Concat(l.Host.Name, "x") }
			})
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | EVAL host.name = CONCAT(host.name, "x")
            | KEEP host.name
            """.NativeLineEndings());
	}

	[Test]
	public void Select_GuardOverAParamsFunctionOfConstantsOnly_ThrowsNotSupported()
	{
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument
			{
				Host = l.Host == null ? null : new NestedSelectionHost { Name = EsqlFunctions.Concat("a", "b") }
			});

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*reads through*");
	}

	[Test]
	public void Select_GuardOverAFunctionThatAnswersNull_ThrowsNotSupported()
	{
		// COALESCE gives a missing value a value of its own, so for a document with no
		// Host the child would carry "x" where the source gives null
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument
			{
				Host = l.Host == null ? null : new NestedSelectionHost { Name = EsqlFunctions.Coalesce(l.Host.Name, "x") }
			});

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*\"Coalesce\"*value of its own*");
	}

	[Test]
	public void Select_GuardOverAnAnonymousChild_IsUnwrapped()
	{
		// an anonymous child is built with new rather than an initializer: its arguments
		// are what has to read through the guarded path
		var esql = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new { Host = l.Host == null ? null : new { l.Host.Name } })
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | KEEP host.name
            """.NativeLineEndings());
	}

	[Test]
	public void Select_GuardOverAnAnonymousChildWithAConstant_ThrowsNotSupported()
	{
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new { Host = l.Host == null ? null : new { l.Host.Name, Label = "constant" } });

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*reads through*");
	}

	[Test]
	public void Select_GuardOnAProjectedValue_IsKeptUnlessTheBranchReadsThroughIt()
	{
		// after Select(n => n.Child) the parameter stands for the child, which may well be
		// null: a constant child would be emitted for it, where the source gives null
		var query = CreateQuery<TreeNode>()
			.From("nodes")
			.Select(n => n.Child)
			.Select(n => new { Wrap = n == null ? null : new { Label = "x" } });

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*reads through*");
	}

	[Test]
	public void Select_GuardOnAProjectedValueReadThrough_IsUnwrapped()
	{
		// the guard is dropped rather than refused, which is what this pins. Chaining a
		// Select onto an object selection names a column the earlier KEEP has already
		// replaced, guarded or not, which is #54 and not asserted here.
		var query = CreateQuery<TreeNode>()
			.From("nodes")
			.Select(n => n.Child)
			.Select(n => new { Wrap = n == null ? null : new { n.Name } });

		var act = () => query.ToString();

		_ = act.Should().NotThrow();
	}

	[Test]
	public void Select_GuardOnTheDocumentRow_StillTakesAConstantChild()
	{
		// the document row is never null, so the guard on it is no guard at all and the
		// constant child is what the source gives
		var esql = CreateQuery<TreeNode>()
			.From("nodes")
			.Select(n => new { Wrap = n == null ? null : new { Label = "x" } })
			.ToString();

		_ = esql.Should().Contain("EVAL wrap.label = \"x\"");
	}

	[Test]
	public void Select_GuardOverASearchFunction_ThrowsNotSupported()
	{
		// MATCH answers a missing field with a definite no rather than null, so the guard
		// cannot be dropped around it
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new { Host = l.Host == null ? null : new { Hit = EsqlFunctions.Match(l.Host.Name, "x") } });

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*\"Match\"*value of its own*");
	}

	[Test]
	public void Select_GuardOverAChildWithAConstructorArgument_ThrowsNotSupported()
	{
		// the nested projection emits the bindings alone, never the constructor's
		// arguments, so a child that takes any is not unwrapped, whatever they read
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new
			{
				Host = l.Host == null ? null : new NestedSelectionHostWithTag("constant") { Name = l.Host.Name }
			});

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*reads through*");
	}

	[Test]
	public void Select_GuardOverAChildWithAConstructorArgumentReadThrough_ThrowsAllTheSame()
	{
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new
			{
				Host = l.Host == null ? null : new NestedSelectionHostWithTag(l.Host.Name) { Name = l.Host.Name }
			});

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*reads through*");
	}

	[Test]
	public void Select_GuardedScalarIntoANonNullableMember_StillProjects()
	{
		// a scalar leaves the column null for a missing value with or without the guard,
		// and the member keeps whatever it holds, exactly as "Message = l.Host!.Name"
		// does: only a child object tells null and an empty object apart
		var esql = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new EagerNestedDocument { Message = l.Host == null ? null! : l.Host.Name })
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | RENAME host.name AS message
            | KEEP message
            """.NativeLineEndings());
	}

	[Test]
	public void Select_GuardedScalarIntoAMemberThatCanHoldNull_IsUnwrapped()
	{
		var esql = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new { Value = l.Host == null ? null : l.Host.Name })
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | RENAME host.name AS value
            | KEEP value
            """.NativeLineEndings());
	}

	[Test]
	public void Select_GuardOverAChildWithANestedInitializer_ThrowsNotSupported()
	{
		// "Geo = { City = ... }" is a binding that is not an assignment: nothing is read
		// into it, so it cannot be said to read through the guarded path. The shape under
		// test is the one the compiler warns about, so the warning is suppressed here.
#pragma warning disable CS8670
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument
			{
				Host = l.Host == null ? null : new NestedSelectionHost { Name = l.Host.Name, Geo = { City = "constant" } }
			});
#pragma warning restore CS8670

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*reads through*");
	}

	[Test]
	public void Select_GuardOverAChildWithAConstantMember_ThrowsNotSupported()
	{
		// one member reads through Host, the other is a constant: for a document with
		// no Host the source gives null, where the constant would give a child with a
		// value in it, so the guard cannot be dropped
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument
			{
				Host = l.Host == null
					? null
					: new NestedSelectionHost { Name = "constant", Geo = new NestedSelectionGeo { City = l.Host!.Geo!.City } }
			});

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*reads through*");
	}

	[Test]
	public void Select_GuardOverAPurelyConstantChild_IsNotUnwrapped()
	{
		// nothing in the child reads through Host, so dropping the guard would give the
		// child a value for a document that has no Host at all
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument
			{
				Host = l.Host == null ? null : new NestedSelectionHost { Name = "constant" }
			});

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*reads through*");
	}

	[Test]
	public void Select_GuardOnTheRowParameterIntoANonNullableMember_StillProjects()
	{
		// the document row is never null, so a guard on the bare parameter is redundant
		// rather than meaningful: dropping it changes nothing about what reaches the row,
		// and the member's own nullability does not come into it
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument { Message = l == null ? null! : l.Message })
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | KEEP message
            """.NativeLineEndings());
	}

	[Test]
	public void Select_GuardOnTheRowParameterIntoANullableValueType_StillProjects()
	{
		// the target is a Nullable<int>, which holds the null a guard produces
		var esql = CreateQuery<LogEntry>()
			.From("logs-*")
			.Select(l => new OptionalCountProjection { Count = l == null ? null : l.StatusCode })
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | RENAME statusCode AS count
            | KEEP count
            """.NativeLineEndings());
	}

	[Test]
	public void Select_GuardIntoANonNullableMemberWithAnInitializer_ThrowsNotSupported()
	{
		// the member is non-nullable and carries an initializer, so a missing parent
		// comes back as that initial value rather than as the guard's null
		var query = CreateQuery<NullableNestedModel>()
			.From("logs-*")
			.Select(l => new EagerNestedDocument
			{
				Host = l.Address == null ? null! : new NestedSelectionHost { Name = l.Address.City }
			});

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*not declared nullable*");
	}

	[Test]
	public void Select_GuardOnTheRowParameterAfterAProjection_ThrowsNotSupported()
	{
		// once a Select has projected, the parameter stands for a value that may be null,
		// so the guard on it is meaningful and the non-nullable member cannot hold its null
		var query = CreateQuery<TreeNode>()
			.From("nodes")
			.Select(n => n.Child!)
			.Select(n => new EagerNestedDocument { Host = n == null ? null! : new NestedSelectionHost { Name = n.Name } });

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*not declared nullable*");
	}

	[Test]
	public void Select_GuardWhoseBranchReadsThroughACast_IsUnwrapped()
	{
		// the guarded path and the path the branch reads are the same member behind a
		// cast, so the branch reads through it and the guard is dropped
		var esql = CreateQuery<NestedSelectionDocument>()
			.From("logs")
			.Select(l => new { N = ((NestedSelectionDocument)(object)l).Host == null ? null : new { ((NestedSelectionDocument)(object)l).Host!.Name } })
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs
            | RENAME host.name AS n.name
            | KEEP n.name
            """.NativeLineEndings());
	}

	[Test]
	public void Select_GuardOverArithmeticOnTheGuardedPath_IsUnwrapped()
	{
		// arithmetic is null over a null operand, like the functions, so an operand that
		// reads through the path carries the null through
		var esql = CreateQuery<NestedSelectionDocument>()
			.From("logs")
			.Select(l => new { Len = l.Host == null ? null : new { Longer = l.Host.Name.Length + 1 } })
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs
            | EVAL len.longer = (LENGTH(host.name) + 1)
            | KEEP len.longer
            """.NativeLineEndings());
	}

	[Test]
	public void Select_GuardOverACoalesceOnTheGuardedPath_ThrowsNotSupported()
	{
		// ?? answers over a null rather than propagating it, so the branch does not read
		// through the path and the guard stands
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs")
			.Select(l => new { V = l.Host == null ? null : new { N = l.Host.Name ?? "x" } });

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*\"??\"*value of its own*");
	}

	[Test]
	public void Select_GuardOverANullComparisonOfTheGuardedPath_ThrowsNotSupported()
	{
		// "!= null" renders as IS NOT NULL, which answers a null with false rather than
		// carrying it through, so a missing host would come back as { Named = false }
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs")
			.Select(l => new { H = l.Host == null ? null : new { Named = l.Host.Name != null } });

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*\"!= null\"*value of its own*");
	}

	[Test]
	public void Select_GuardOverIsNotNullOfTheGuardedPath_ThrowsNotSupported()
	{
		// the marker spelling of the same test is refused the same way
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs")
			.Select(l => new { H = l.Host == null ? null : new { Named = EsqlFunctions.IsNotNull(l.Host.Name) } });

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*\"IsNotNull\"*value of its own*");
	}

	[Test]
	public void Select_ConditionalTestingNullWithoutGuardingAPath_TranslatesWithIsNull()
	{
		// not a guard over a path the branch reads, so it is a plain CASE: the test is
		// IS NULL, where the C# operator would answer null for every row
#pragma warning disable IDE0029 // the "== null ? :" form is the shape under test; ?? would translate down another path
		var esql = CreateQuery<LogEntry>()
			.From("logs")
			.Select(l => new { V = l.ClientIp == null ? "none" : l.ClientIp })
			.ToString();
#pragma warning restore IDE0029

		_ = esql.Should().Be(
			"""
            FROM logs
            | EVAL v = CASE WHEN (clientIp IS NULL) THEN "none" ELSE clientIp END
            | KEEP v
            """.NativeLineEndings());
	}

	[Test]
	public void Select_GuardWhoseNullIsBehindAConvert_IsUnwrapped()
	{
		// a hand-built tree types the null literal by wrapping it in a Convert, which is
		// unwrapped rather than reaching the fallback and emitting a "== null" comparison
		var parameter = Expression.Parameter(typeof(NestedSelectionDocument), "l");
		var host = Expression.Property(parameter, nameof(NestedSelectionDocument.Host));
		var guard = Expression.Condition(
			Expression.Equal(host, Expression.Constant(null, typeof(NestedSelectionHost))),
			Expression.Convert(Expression.Constant(null, typeof(object)), typeof(string)),
			Expression.Property(host, nameof(NestedSelectionHost.Name)));
		var selector = Expression.Lambda<Func<NestedSelectionDocument, NestedSelectionGeo>>(
			Expression.MemberInit(
				Expression.New(typeof(NestedSelectionGeo)),
				Expression.Bind(typeof(NestedSelectionGeo).GetProperty(nameof(NestedSelectionGeo.City))!, guard)),
			parameter);

		var esql = CreateQuery<NestedSelectionDocument>().From("logs").Select(selector).ToString();

		_ = esql.Should().Be(
			"""
            FROM logs
            | RENAME host.name AS city
            | KEEP city
            """.NativeLineEndings());
	}

	[Test]
	public void Select_GuardOnTheLookupSideOverAFunctionThatAnswersNull_FoldsIntoACase()
	{
		// the fold keeps the guard, as IS NOT NULL over the branch's columns, so the branch
		// has only to read the lookup side, not to carry its null through
		var lookup = CreateQuery<LanguageLookup>().From("languages_lookup");

		var esql = CreateQuery<LogEntry>()
			.From("employees")
			.LeftJoin(lookup, o => o.StatusCode, i => i.LanguageCode,
				(o, i) => new { o.Message, V = i == null ? null : EsqlFunctions.Coalesce(i.LanguageName, "x") })
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM employees
            | LOOKUP JOIN languages_lookup ON statusCode == languageCode
            | EVAL v = CASE WHEN languageName IS NOT NULL THEN COALESCE(languageName, "x") ELSE NULL END
            | KEEP message, v
            """.NativeLineEndings());
	}

	[Test]
	public void Select_GuardOnTheLookupSideOfAJoin_IsKeptUnlessTheBranchReadsThroughIt()
	{
		// a row with no match leaves the lookup parameter null, unlike the document row,
		// so a guard on it says something and a child that reads elsewhere keeps it
		var lookup = CreateQuery<LanguageLookup>().From("languages_lookup");

		var query = CreateQuery<LogEntry>()
			.From("employees")
			.LeftJoin(lookup, o => o.StatusCode, i => i.LanguageCode,
				(o, i) => new { o.Message, Lang = i == null ? null : new { Label = o.Message } });

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*reads through*");
	}
}
