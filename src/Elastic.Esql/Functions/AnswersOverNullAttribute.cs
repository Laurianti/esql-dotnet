// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

namespace Elastic.Esql.Functions;

/// <summary>
/// Marks a function that answers a null input with a value of its own rather than with
/// null: COALESCE and the null tests, which report on the null, and the search functions,
/// which answer a missing field with a definite no.
/// <para>
/// A projection drops a null guard around a function of the guarded path, which is exact
/// only while the function is null wherever the path is. Marking the exceptions rather
/// than listing the functions that propagate makes the default "keep the guard", so a
/// marker added later is refused until it says otherwise.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AnswersOverNullAttribute : Attribute;
