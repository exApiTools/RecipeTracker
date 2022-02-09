using System.Collections.Generic;
using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using Newtonsoft.Json;
using SharpDX;

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

        [JsonIgnore]
        public ButtonNode ReloadRecipeBooks { get; set; } = new ButtonNode();

        [Menu("Toggle window key")]
        public HotkeyNode ToggleWindowKey { get; set; } = new HotkeyNode(Keys.NumPad4);

        public ToggleNode ShowWindowOnZoneChange { get; set; } = new ToggleNode(false);
        public ToggleNode ShowWindowWhenArchnemesisInventoryOpens { get; set; } = new ToggleNode(false);
        public ToggleNode HideWindowWhenArchnemesisInventoryCloses { get; set; } = new ToggleNode(false);
        public ToggleNode RemeberRecipeOnZoneChange { get; set; } = new ToggleNode(false);
        public ToggleNode HideUndesiredRecipesFromTracker { get; set; } = new ToggleNode(false);
        public ToggleNode CacheGroundItemPosition { get; set; } = new ToggleNode(true);
        public ColorNode UndesiredItemColor { get; set; } = new ColorNode(Color.Red);
        public ColorNode RecipeItemColor { get; set; } = new ColorNode(Color.Green);
        public ColorNode DesiredItemColor { get; set; } = new ColorNode(Color.Yellow);
        public ColorNode PutInNowItemColor { get; set; } = new ColorNode(Color.Blue);
        public RangeNode<int> InventoryCrossThickness { get; set; } = new RangeNode<int>(1, 1, 10);
        public RangeNode<int> GroundCrossThickness { get; set; } = new RangeNode<int>(5, 1, 10);
        public RangeNode<float> OrderTextSize { get; set; } = new RangeNode<float>(1, 0, 1);

        public ListNode RecipeToWorkTowards { get; set; } = new ListNode() { Value = NoRecipeSelected };

        public HashSet<string> DesiredRecipes { get; set; } = new HashSet<string>();
    }
}
