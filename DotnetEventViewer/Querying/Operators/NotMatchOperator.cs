using System.Text.RegularExpressions;
using EventPipe;

namespace DotnetEventViewer.Querying.Operators;

internal class NotMatchOperator : IOperator
{
    public static NotMatchOperator Instance { get; } = new();

    public string Text => "≄";
    public bool IsCompatible(TypeCode code) => code != TypeCodeExtensions.Array;
    public bool Match(object evtFieldValue, object filterValue) => !((Regex)filterValue).IsMatch(evtFieldValue.ToString()!);
}