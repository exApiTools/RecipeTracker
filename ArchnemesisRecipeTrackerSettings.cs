using System.Collections.Generic;
using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using Newtonsoft.Json;

namespace ArchnemesisRecipeTracker
{
    public class ArchnemesisRecipeTrackerSettings : ISettings
    {
        internal const string NoRecipeSelected = "(none)";

        public ArchnemesisRecipeTrackerSettings()
        {
            Enable = new ToggleNode(false);
        }

        public ToggleNode Enable { get; set; } = new ToggleNode(false);
        public ToggleNode ShowWindowOnZoneChange { get; set; } = new ToggleNode(false);
        public ToggleNode ShowWindowWhenArchnemesisInventoryOpens { get; set; } = new ToggleNode(false);
        public ToggleNode HideWindowWhenArchnemesisInventoryCloses { get; set; } = new ToggleNode(false);
        public ToggleNode RemeberRecipeOnZoneChange { get; set; } = new ToggleNode(false);


        [JsonIgnore]
        public ButtonNode ReloadRecipeBooks { get; set; } = new ButtonNode();

        [Menu("Toggle window key")]
        public HotkeyNode ToggleWindowKey { get; set; } = new HotkeyNode(Keys.NumPad4);

        public ListNode RecipeToWorkTowards { get; set; } = new ListNode() { Value = NoRecipeSelected };

        public HashSet<string> DesiredRecipes { get; set; } = new HashSet<string>();
    }
}
