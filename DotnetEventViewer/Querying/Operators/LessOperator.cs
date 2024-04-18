using EventPipe;

namespace DotnetEventViewer.Querying.Operators;

internal class LessOperator : IOperator
{
    public static LessOperator Instance { get; } = new();

    public string Text => "<";
    public bool IsCompatible(TypeCode code) => code != TypeCodeExtensions.Array;
    public bool Match(object evtFieldValue, object filterValue) => ((IComparable)evtFieldValue).CompareTo(filterValue) < 0;
}