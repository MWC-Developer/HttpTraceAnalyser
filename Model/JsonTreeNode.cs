using System.Collections.Generic;

namespace HttpTraceAnalyser.Model
{
    /// <summary>
    /// A single node in a tree representation of a parsed JSON document, used to populate
    /// a WPF <see cref="System.Windows.Controls.TreeView"/> so that objects and arrays can be
    /// expanded/collapsed independently (e.g. a large "messages" array in a REST response).
    /// </summary>
    public sealed class JsonTreeNode
    {
        public JsonTreeNode(string displayText, bool isExpanded = false)
        {
            DisplayText = displayText;
            IsExpanded = isExpanded;
        }

        /// <summary>The rendered "name: value" (or "name [n]" / "name {n}") text for this node.</summary>
        public string DisplayText { get; }

        /// <summary>Child nodes, for object/array values. Empty for scalar leaves.</summary>
        public List<JsonTreeNode> Children { get; } = new();

        /// <summary>Whether the corresponding TreeViewItem should start expanded.</summary>
        public bool IsExpanded { get; set; }
    }
}
