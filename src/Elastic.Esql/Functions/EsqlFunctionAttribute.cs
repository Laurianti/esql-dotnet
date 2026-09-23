// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

namespace Elastic.Esql.Functions;

/// <summary>
/// Describes what the translation may assume about an <see cref="EsqlFunctions"/> marker.
/// <para>
/// A marker that carries none is assumed to answer a null input with a value of its own,
/// so a null guard around it is kept and the shape is refused: a marker nobody has thought
/// about is refused until someone does.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class EsqlFunctionAttribute : Attribute
{
	/// <summary>
	/// True when the ES|QL function is null wherever an input is null, which lets a
	/// projection drop a null guard around a call of the guarded path.
	/// </summary>
	public bool PropagatesNull { get; init; }
}
