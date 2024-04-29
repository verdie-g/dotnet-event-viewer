using DotnetEventViewer.CallTree.CountAggregators;
using EventPipe;

namespace DotnetEventViewer.CallTree;

public class CallTreeNode
{
    public static CallTreeNode Create(
        IEnumerable<Event> events,
        ICallTreeCountAggregatorProcessor processor)
    {
        CallTreeNode root = new(0, new MethodDescription("root", ""));

        int id = 1;
        // Process in reverse order so that ProcessEvent observes a stop event before its associated start event.
        foreach (var evt in events.Reverse())
        {
            processor.ProcessEvent(evt);

            var currentNode = root;

            for (int i = 0; i < evt.StackTrace.Frames.Length; i += 1)
            {
                var frame = evt.StackTrace.Frames[i];
                currentNode.Children ??= new Dictionary<ulong, CallTreeNode>();
                if (!currentNode.Children.TryGetValue(frame.Address, out var childNode))
                {
                    childNode = new CallTreeNode(id, frame);
                    currentNode.Children[frame.Address] = childNode;
                    id += 1;
                }

                currentNode = childNode;

                if (i == evt.StackTrace.Frames.Length - 1)
                {
                    processor.UpdateLeafNode(ref currentNode._count, evt);
                }
            }
        }

        root.PropagateCount();

        return root;
    }

    private long _count;

    private CallTreeNode(int id, MethodDescription methodDescription)
    {
        Id = id;
        MethodDescription = methodDescription;
    }

    public int Id { get; }
    public Dictionary<ulong, CallTreeNode>? Children { get; private set; }
    public MethodDescription MethodDescription { get; }
    public long Count => _count;

    public override string ToString()
    {
        return MethodDescription.ToString();
    }

    private long PropagateCount()
    {
        if (Children != null)
        {
            foreach (var child in Children)
            {
                _count += child.Value.PropagateCount();
            }
        }

        return _count;
    }
}