using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Cache;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using Newtonsoft.Json;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace ArchnemesisRecipeTracker
{
    public class NextSteps
    {
        public List<string> ItemsToCollect { get; set; } = new List<string>();
        public List<string> RecipesToRun { get; set; } = new List<string>();

        public NextSteps Merge(NextSteps other)
        {
            return new NextSteps
            {
                ItemsToCollect = ItemsToCollect.Concat(other.ItemsToCollect).ToList(),
                RecipesToRun = RecipesToRun.Concat(other.RecipesToRun).ToList()
            };
        }

        public static NextSteps Empty => new NextSteps();
    }

    public class ArchnemesisRecipeTracker : BaseSettingsPlugin<ArchnemesisRecipeTrackerSettings>
    {
        private readonly CachedValue<IEnumerable<(Element element, string itemDisplayName)>> _itemsOnGroundCache;
        private readonly CachedValue<IEnumerable<(ArchnemesisInventorySlot element, string itemDisplayName)>> _inventoryElementsCache;

        private bool windowState;

        private Dictionary<string, RecipeEntry> _recipesByResult = new Dictionary<string, RecipeEntry>();
        private RecipeEntry _trackedRecipe;
        private HashSet<string> _desiredComponentCache = new HashSet<string>();
        private HashSet<string> _alreadyPutInCache = new HashSet<string>();
        private Dictionary<string, int> _presentIngredientCache = new Dictionary<string, int>();
        private Dictionary<string, RecipeEntry> _recipesByName = new Dictionary<string, RecipeEntry>();
        private bool _inventoryWasShownOnLastFrame = false;

        public ArchnemesisRecipeTracker()
        {
            _itemsOnGroundCache = new TimeCache<IEnumerable<(Element, string)>>(() =>
                GameController.IngameState.IngameUi.ItemsOnGroundLabelsVisible
                   .Where(x => x.ItemOnGround?.GetComponent<WorldItem>()?.ItemEntity?.Path ==
                               "Metadata/Items/Archnemesis/ArchnemesisMod")
                   .Select(x => (x.Label, x.Label.Text))
                   .ToList(), 500);
            _inventoryElementsCache = new TimeCache<IEnumerable<(ArchnemesisInventorySlot, string)>>(() =>
                GameController.IngameState.IngameUi.ArchnemesisInventoryPanel.InventoryElements.Select(x => (x, x.Item.DisplayName))
                   .ToList(), 200);
        }

        public override void OnLoad()
        {
        }

        public override bool Initialise()
        {
            Input.RegisterKey(Settings.ToggleWindowKey);
            Settings.ToggleWindowKey.OnValueChanged += () => { Input.RegisterKey(Settings.ToggleWindowKey); };
            Settings.ReloadRecipeBooks.OnPressed += ReloadRecipeBook;
            Settings.RecipeToWorkTowards.OnValueSelected += newValue =>
            {
                if (_recipesByName.ContainsKey(newValue))
                {
                    Settings.DesiredRecipes.Add(newValue);
                    RebuildDesiredComponentCache();
                }
            };
            ReloadRecipeBook();
            RebuildDesiredComponentCache();
            return true;
        }

        public override void AreaChange(AreaInstance area)
        {
            _inventoryWasShownOnLastFrame = false;
            _alreadyPutInCache = new HashSet<string>();
            if (!Settings.RemeberRecipeOnZoneChange)
            {
                _trackedRecipe = null;
            }

            if (Settings.ShowWindowOnZoneChange)
            {
                windowState = true;
            }
        }

        private void RebuildDesiredComponentCache()
        {
            try
            {
                _desiredComponentCache = GetDesiredComponents();
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"{Name}: Failed to rebuild the desired component list: {ex}");
            }
        }

        private void ReloadRecipeBook()
        {
            try
            {
                var defaultRecipeBookPath = Path.Combine(DirectoryFullName, "defaultRecipeBook.json");
                var defaultRecipeBook = JsonConvert.DeserializeObject<List<RecipeEntry>>(File.ReadAllText(defaultRecipeBookPath)) ??
                                        throw new Exception($"null in {defaultRecipeBookPath}");
                var customRecipeBookPath = Path.Combine(DirectoryFullName, "customRecipeBook.json");
                if (!File.Exists(customRecipeBookPath))
                {
                    File.Copy(Path.Combine(DirectoryFullName, "customRecipeBook.example.json"), customRecipeBookPath, false);
                }

                var customRecipeBook = JsonConvert.DeserializeObject<List<RecipeEntry>>(File.ReadAllText(customRecipeBookPath)) ??
                                       throw new Exception($"null in {customRecipeBookPath}");
                var fullRecipeBook = defaultRecipeBook.Concat(customRecipeBook).ToList();
                var duplicateNames = fullRecipeBook.GroupBy(x => x.Name).Where(x => x.Count() > 1).ToList();
                if (duplicateNames.Any())
                {
                    throw new Exception($"Duplicate recipe names: {string.Join(", ", duplicateNames.Select(x => x.Key))}");
                }

                var duplicateResults = fullRecipeBook.Where(x => x.Result != null).GroupBy(x => x.Result).Where(x => x.Count() > 1).ToList();
                if (duplicateResults.Any())
                {
                    throw new Exception($"Duplicate recipe results: {string.Join(", ", duplicateResults.Select(x => x.Key))}");
                }

                _recipesByResult = fullRecipeBook.Where(x => x.Result != null).ToDictionary(x => x.Result);
                _recipesByName = fullRecipeBook.ToDictionary(x => x.Name);
                Settings.RecipeToWorkTowards.SetListValues(_recipesByName.Keys.OrderBy(x => x).Prepend(ArchnemesisRecipeTrackerSettings.NoRecipeSelected).ToList());
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"{Name}: Failed to rebuild the recipe book: {ex}");
            }
        }

        private HashSet<RecipeEntry> GetRecipesWithEnoughIngredients(ICollection<string> presentIngredients)
        {
            return _recipesByName.Values.Where(x => x.Recipe.All(presentIngredients.Contains)).ToHashSet();
        }

        private HashSet<string> GetDesiredComponents()
        {
            var set = new HashSet<string>();

            void FillSet(string component)
            {
                if (!set.Add(component) ||
                    !_recipesByResult.TryGetValue(component, out var recipe))
                {
                    return;
                }

                foreach (var ingredient in recipe.Recipe)
                {
                    FillSet(ingredient);
                }
            }

            foreach (var desiredRecipeName in Settings.DesiredRecipes)
            {
                if (!_recipesByName.TryGetValue(desiredRecipeName, out var desiredRecipe))
                {
                    continue;
                }

                if (desiredRecipe.Result != null)
                {
                    FillSet(desiredRecipe.Result);
                }
                else
                {
                    foreach (var ingredient in desiredRecipe.Recipe)
                    {
                        FillSet(ingredient);
                    }
                }
            }

            return set;
        }

        public NextSteps GetNextSteps(RecipeEntry recipe, Dictionary<string, int> components)
        {
            var result = NextSteps.Empty;
            var allIngredientsPresent = true;
            foreach (var ingredient in recipe.Recipe)
            {
                if (components.TryGetValue(ingredient, out var count) && count > 0)
                {
                    components[ingredient] = count - 1;
                }
                else
                {
                    allIngredientsPresent = false;
                    result = result.Merge(
                        _recipesByResult.TryGetValue(ingredient, out var subRecipe)
                            ? GetNextSteps(subRecipe, components)
                            : new NextSteps { ItemsToCollect = { ingredient } });
                }
            }

            if (allIngredientsPresent)
            {
                result = result.Merge(new NextSteps { RecipesToRun = { recipe.Name } });
            }

            return result;
        }

        public override void DrawSettings()
        {
            base.DrawSettings();
            if (ImGui.TreeNode("Select desired recipes"))
            {
                foreach (var recipeEntry in _recipesByName.Values.OrderBy(x => x.Name))
                {
                    var ticked = Settings.DesiredRecipes.Contains(recipeEntry.Name);
                    if (ImGui.Checkbox(recipeEntry.Name, ref ticked))
                    {
                        if (ticked)
                        {
                            Settings.DesiredRecipes.Add(recipeEntry.Name);
                        }
                        else
                        {
                            Settings.DesiredRecipes.Remove(recipeEntry.Name);
                        }

                        RebuildDesiredComponentCache();
                    }
                }
            }
        }

        private void RenderRecipeLine(RecipeEntry recipe, Color color)
        {
            var ticked = _trackedRecipe == recipe;
            ImGui.PushStyleColor(ImGuiCol.Text, color.ToImgui());
            if (ImGui.Checkbox(recipe.Name, ref ticked))
            {
                if (!ticked)
                {
                    _trackedRecipe = null;
                }

                if (ticked)
                {
                    _trackedRecipe = recipe;
                }
            }

            ImGui.PopStyleColor();
        }

        public override void Render()
        {
            if (!GameController.InGame)
            {
                return;
            }

            if (Settings.ToggleWindowKey.PressedOnce())
            {
                windowState = !windowState;
            }

            if (!_recipesByName.TryGetValue(Settings.RecipeToWorkTowards.Value ?? ArchnemesisRecipeTrackerSettings.NoRecipeSelected, out var recipeToWorkOn) &&
                Settings.RecipeToWorkTowards.Value != ArchnemesisRecipeTrackerSettings.NoRecipeSelected)
            {
                Settings.RecipeToWorkTowards.Value = ArchnemesisRecipeTrackerSettings.NoRecipeSelected;
            }

            var inventory = GameController.IngameState.IngameUi.ArchnemesisInventoryPanel;
            var inputPanel = GameController.IngameState.IngameUi.ArchnemesisAltarPanel;

            if (Settings.ShowWindowWhenArchnemesisInventoryOpens && !_inventoryWasShownOnLastFrame && inventory.IsVisible)
            {
                windowState = true;
            }

            if (Settings.HideWindowWhenArchnemesisInventoryCloses && _inventoryWasShownOnLastFrame && !inventory.IsVisible)
            {
                windowState = false;
            }

            _inventoryWasShownOnLastFrame = inventory.IsVisible;

            var alreadyPutIn = inputPanel.IsVisible
                                   ? _alreadyPutInCache = inputPanel.InventoryElements.Select(x => x.Item.DisplayName).ToHashSet()
                                   : _alreadyPutInCache;
            var presentIngredients =
                inventory.IsVisible
                    ? _presentIngredientCache =
                          _inventoryElementsCache.Value
                             .Select(x => x.itemDisplayName)
                             .Concat(alreadyPutIn)
                             .GroupBy(x => x)
                             .ToDictionary(x => x.Key, x => x.Count())
                    : _presentIngredientCache;
            if (windowState)
            {
                ImGui.Begin($"{Name}", ref windowState);
                var nextSteps = NextSteps.Empty;
                if (recipeToWorkOn != null)
                {
                    ImGui.Text($"Working towards {recipeToWorkOn.Name}.");
                    nextSteps = GetNextSteps(recipeToWorkOn, new Dictionary<string, int>(presentIngredients));
                    if (nextSteps.RecipesToRun.Any())
                    {
                        ImGui.Text($"Run {string.Join(", ", nextSteps.RecipesToRun.GroupBy(x => x).Select(x => x.Count() == 1 ? x.Key : $"{x.Count()}x {x.Key}"))}");
                    }

                    if (nextSteps.ItemsToCollect.Any())
                    {
                        ImGui.Text($"Collect {string.Join(", ", nextSteps.ItemsToCollect.GroupBy(x => x).Select(x => x.Count() == 1 ? x.Key : $"{x.Count()}x {x.Key}"))}");
                    }

                    if (!nextSteps.ItemsToCollect.Any() && !nextSteps.RecipesToRun.Any())
                    {
                        ImGui.Text("Umm... nothing to be done! This isn't supposed to happen, but ok");
                    }
                }

                if (ImGui.TreeNodeEx("Select recipe to build in this map", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    var recipesWithEnoughIngredients = GetRecipesWithEnoughIngredients(presentIngredients.Keys);
                    var nextStepRecipeNames = nextSteps.RecipesToRun.ToHashSet();
                    if (_trackedRecipe != null && !recipesWithEnoughIngredients.Contains(_trackedRecipe))
                    {
                        RenderRecipeLine(_trackedRecipe, Settings.RecipeItemColor);
                        ImGui.SameLine();
                        ImGui.TextColored(Color.Yellow.ToImguiVec4(), "(not enough ingredients)");
                        ImGui.Separator();
                    }

                    var recipesToBuildTrackedItem = recipesWithEnoughIngredients
                       .Where(x => nextStepRecipeNames.Contains(x.Name))
                       .OrderBy(x => x.Name)
                       .ToList();
                    if (recipesToBuildTrackedItem.Any())
                    {
                        foreach (var recipe in recipesToBuildTrackedItem)
                        {
                            RenderRecipeLine(recipe, Settings.RecipeItemColor);
                        }

                        ImGui.Separator();
                    }

                    recipesWithEnoughIngredients.ExceptWith(recipesToBuildTrackedItem);

                    var desiredRecipes = recipesWithEnoughIngredients
                       .Where(x => Settings.DesiredRecipes.Contains(x.Name) ||
                                   _desiredComponentCache.Contains(x.Result))
                       .OrderBy(x => x.Name)
                       .ToList();
                    if (desiredRecipes.Any())
                    {
                        ImGui.Text("These are not worked towards currently,");
                        ImGui.Text("but are marked as desired");
                        foreach (var recipe in desiredRecipes)
                        {
                            RenderRecipeLine(recipe, Settings.DesiredItemColor);
                        }

                        ImGui.Separator();
                    }

                    recipesWithEnoughIngredients.ExceptWith(desiredRecipes);
                    if (!Settings.HideUndesiredRecipesFromTracker && recipesWithEnoughIngredients.Any())
                    {
                        ImGui.Text("These are not marked as desired, but are");
                        ImGui.Text("just there for you to know you can do them");
                        foreach (var recipe in recipesWithEnoughIngredients.OrderBy(x => x.Name))
                        {
                            RenderRecipeLine(recipe, Settings.UndesiredItemColor);
                        }
                    }
                }

                ImGui.End();
            }

            ImGui.Begin("lmao",
                ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav);
            var drawList = ImGui.GetBackgroundDrawList();
            if (inventory.IsVisible && inputPanel.IsVisible && _trackedRecipe != null)
            {
                var recipeSet = _trackedRecipe.Recipe.ToHashSet();
                var panelClientRect = inputPanel.GetClientRect();
                var textDisplayPosition = new Vector2(panelClientRect.Left, panelClientRect.Center.Y);

                void DrawText(string text, Vector2 position, Color color, float scale = 1)
                {
                    ImGui.SetWindowFontScale(scale);
                    var boxRect = ImGui.CalcTextSize(text);
                    drawList.AddRectFilled(position, position + boxRect, Color.Black.ToImgui());
                    drawList.AddText(position, color.ToImgui(), text);
                    ImGui.SetWindowFontScale(1);
                }

                if (alreadyPutIn.SetEquals(recipeSet))
                {
                    DrawText($"Recipe {_trackedRecipe.Name} is complete!", textDisplayPosition, Color.Green);
                }
                else if (recipeSet.IsProperSupersetOf(presentIngredients.Keys))
                {
                    DrawText($"You are missing components of recipe {_trackedRecipe.Name}: " +
                             string.Join(", ", recipeSet.Except(presentIngredients.Keys)), textDisplayPosition, Color.Yellow);
                }
                else
                {
                    if (!alreadyPutIn.IsSubsetOf(recipeSet))
                    {
                        DrawText($"You put in components the recipe {_trackedRecipe.Name} doesn't use: " +
                                 string.Join(", ", alreadyPutIn.Except(recipeSet)), textDisplayPosition, Color.Yellow);
                    }

                    var itemMap = _trackedRecipe.Recipe.Except(alreadyPutIn).Select((name, i) => (name, i))
                       .ToDictionary(x => x.name, x => x.i + 1);
                    foreach (var (element, itemDisplayName) in _inventoryElementsCache.Value)
                    {
                        if (itemMap.TryGetValue(itemDisplayName, out var itemIndex))
                        {
                            var elementRect = element.GetClientRectCache;
                            elementRect.Inflate(-2, -2);
                            DrawText(itemIndex.ToString(), elementRect.TopLeft.ToVector2Num(),
                                itemIndex == 1 ? Settings.PutInNowItemColor : Settings.RecipeItemColor,
                                1 + Settings.OrderTextSize * (elementRect.Height / ImGui.CalcTextSize(itemIndex.ToString()).Y - 1));
                        }
                    }
                }
            }

            var crossedElements = _itemsOnGroundCache.Value.Select(x =>
                (x.element, x.itemDisplayName, Settings.GroundCrossThickness.Value, Settings.CacheGroundItemPosition.Value));
            if (inventory.IsVisible)
            {
                crossedElements = crossedElements.Concat(_inventoryElementsCache.Value.Select(x =>
                    ((Element)x.element, x.itemDisplayName, Settings.InventoryCrossThickness.Value, true)));
            }

            foreach (var (element, name, thickness, useCachePosition) in crossedElements)
            {
                if (!_desiredComponentCache.Contains(name) &&
                    (_trackedRecipe == null || !_trackedRecipe.Recipe.Contains(name)))
                {
                    var elementRect = useCachePosition ? element.GetClientRectCache : element.GetClientRect();
                    elementRect.Inflate(-2, -2);

                    drawList.AddLine(elementRect.TopLeft.ToVector2Num(), elementRect.BottomRight.ToVector2Num(), Settings.UndesiredItemColor.Value.ToImgui(), thickness);
                    drawList.AddLine(elementRect.BottomLeft.ToVector2Num(), elementRect.TopRight.ToVector2Num(), Settings.UndesiredItemColor.Value.ToImgui(), thickness);
                }
            }

            ImGui.End();
        }
    }
}
