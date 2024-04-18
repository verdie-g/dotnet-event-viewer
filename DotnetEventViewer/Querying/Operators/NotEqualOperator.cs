using EventPipe;

namespace DotnetEventViewer.Querying.Operators;

internal class NotEqualOperator : IOperator
{
    public static NotEqualOperator Instance { get; } = new();

    public string Text => "≠";
    public bool IsCompatible(TypeCode code) => code != TypeCodeExtensions.Array;
    public bool Match(object evtFieldValue, object filterValue) => !evtFieldValue.Equals(filterValue);
}