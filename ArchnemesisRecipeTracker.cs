using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
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
        private static readonly Assembly _assembly = typeof(ArchnemesisRecipeTracker).Assembly;

        private readonly CachedValue<IEnumerable<(Element element, string itemDisplayName)>> _itemsOnGroundCache;
        private readonly CachedValue<IEnumerable<(ArchnemesisInventorySlot element, string itemDisplayName)>> _inventoryElementsCache;

        private bool windowState;

        private Dictionary<string, RecipeEntry> _recipesByResult = new Dictionary<string, RecipeEntry>();
        private RecipeEntry _trackedRecipe;
        private HashSet<string> _desiredComponentCache = new HashSet<string>();
        private List<string> _alreadyPutInCache = new List<string>();
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

        public override bool Initialise()
        {
            Input.RegisterKey(Settings.ToggleWindowKey);
            Settings.ToggleWindowKey.OnValueChanged += () => { Input.RegisterKey(Settings.ToggleWindowKey); };
            Settings.ReloadRecipeBooks.OnPressed += ReloadRecipeBook;
            Settings.ExportDefaultRecipeBook.OnPressed += ExportDefaultRecipeBook;
            Settings.RecipeToWorkTowards.OnValueSelected += newValue =>
            {
                if (_recipesByName.ContainsKey(newValue))
                {
                    Settings.DesiredRecipes.Add(newValue);
                    RebuildRecipeToWorkTowardList();
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
            _alreadyPutInCache = new List<string>();
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

        private void ExportDefaultRecipeBook()
        {
            var defaultBookPath = Path.Combine(DirectoryFullName, "defaultRecipeBook.json");
            var defaultBook = GetDefaultRecipeBook().OrderBy(x => x.Name);
            File.WriteAllText(defaultBookPath, JsonConvert.SerializeObject(defaultBook, Formatting.Indented));
        }

        private List<RecipeEntry> GetDefaultRecipeBook()
        {
            if (Settings.DisableAutomaticDefaultRecipeBook)
            {
                var recipeBookStream = _assembly.GetManifestResourceStream("defaultRecipeBook.json") ??
                                       throw new Exception("Embedded defaultRecipeBook.json is missing");
                using var defaultBookReader = new StreamReader(recipeBookStream);
                var text = defaultBookReader.ReadToEnd();
                var defaultRecipeBook = JsonConvert.DeserializeObject<List<RecipeEntry>>(text) ??
                                        throw new Exception("null in default recipe book");
                return defaultRecipeBook;
            }

            return GameController.Files.ArchnemesisRecipes.EntriesList.Select(x => new RecipeEntry
            {
                Name = x.Outcome.DisplayName,
                Result = x.Outcome.DisplayName,
                Recipe = x.Components.Select(c => c.DisplayName).ToList()
            }).ToList();
        }

        private void ReloadRecipeBook()
        {
            try
            {
                var fullRecipeBook = new List<RecipeEntry>();
                fullRecipeBook.AddRange(GetDefaultRecipeBook());

                var customRecipeBookPath = Path.Combine(DirectoryFullName, "customRecipeBook.json");
                if (!File.Exists(customRecipeBookPath))
                {
                    using var exampleStream = _assembly.GetManifestResourceStream("customRecipeBook.example.json");
                    if (exampleStream == null)
                    {
                        throw new Exception("Embedded customRecipeBook.example.json is missing");
                    }

                    using var fileStream = File.Open(customRecipeBookPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
                    exampleStream.CopyTo(fileStream);
                }

                var customRecipeBook = JsonConvert.DeserializeObject<List<RecipeEntry>>(File.ReadAllText(customRecipeBookPath)) ??
                                       throw new Exception($"null in {customRecipeBookPath}");
                fullRecipeBook.AddRange(customRecipeBook);
                var nullNames = fullRecipeBook.Where(x => x.Name == null).ToList();
                if (nullNames.Any())
                {
                    throw new Exception("You have recipes with a missing name. I can't handle this:(");
                }

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

                var emptyRecipes = fullRecipeBook.Where(x => x.Recipe == null || x.Recipe.Count == 0).ToList();
                if (emptyRecipes.Any())
                {
                    throw new Exception($"You have recipes with an empty or missing recipe ({string.Join(",", emptyRecipes.Select(x => x.Name))}). I can't handle this:(");
                }

                _recipesByResult = fullRecipeBook.Where(x => x.Result != null).ToDictionary(x => x.Result);
                _recipesByName = fullRecipeBook.ToDictionary(x => x.Name);
                _trackedRecipe = null;
                RebuildRecipeToWorkTowardList();
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"{Name}: Failed to rebuild the recipe book: {ex}");
            }
        }

        private void RebuildRecipeToWorkTowardList()
        {
            Settings.RecipeToWorkTowards.SetListValues(_recipesByName.Keys
               .Where(x => !Settings.HideUndesiredRecipesFromWorkTowardSelector || Settings.DesiredRecipes.Contains(x))
               .OrderBy(x => x).Prepend(ArchnemesisRecipeTrackerSettings.NoRecipeSelected).ToList());
        }

        private HashSet<RecipeEntry> GetRecipesWithEnoughIngredients(ICollection<string> presentIngredients, ICollection<string> excludedIngredients,
                                                                     ICollection<string> putInIngredients)
        {
            return _recipesByName.Values
               .Where(x => x.Recipe.Union(putInIngredients).Take(4 + 1).Count() <= 4)
               .Where(x => !x.Recipe.Any(excludedIngredients.Contains))
               .Where(x => x.Recipe.All(presentIngredients.Contains))
               .ToHashSet();
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

                        RebuildRecipeToWorkTowardList();
                        RebuildDesiredComponentCache();
                    }
                }
            }
        }

        private void RenderRecipeLine(RecipeEntry recipe, Color color, HashSet<RecipeEntry> completedRecipes, HashSet<string> putInIngredients)
        {
            var recipeIsCompleted = completedRecipes.Contains(recipe);
            if (recipeIsCompleted && _trackedRecipe == recipe)
            {
                _trackedRecipe = null;
            }

            var ticked = _trackedRecipe == recipe;
            ImGui.PushStyleColor(ImGuiCol.Text, (recipeIsCompleted ? Color.Gray : color).ToImgui());

            if (ImGui.Checkbox(recipe.Name, ref ticked))
            {
                _trackedRecipe = ticked ? recipe : null;
            }

            ImGui.PopStyleColor();
            if (recipeIsCompleted)
            {
                ImGui.SameLine();
                ImGui.TextColored(Color.Green.ToImguiVec4(), "(completed)");
            }
            else
            {
                if (!Settings.DisplayRecipeComponents)
                {
                    ImGui.SameLine();
                }

                var sb = new StringBuilder(100);
                sb.Append("(");
                sb.Append(recipe.Recipe.Count);
                sb.Append(" ingredient");
                if (recipe.Recipe.Count != 1)
                {
                    sb.Append('s');
                }

                if (Settings.DisplayRecipeComponents)
                {
                    sb.Append(": ");
                    foreach (var ingredient in recipe.Recipe)
                    {
                        if (putInIngredients.Contains(ingredient))
                        {
                            ImGui.Text(sb.ToString());
                            sb.Clear();
                            ImGui.SameLine(0, 0);
                            ImGui.TextColored(Color.Green.ToImguiVec4(), ingredient);
                            ImGui.SameLine(0, 0);
                        }
                        else
                        {
                            sb.Append(ingredient);
                        }

                        sb.Append(", ");
                    }

                    sb.Length -= 2;

                    if (recipe.Result != null && recipe.Result != recipe.Name)
                    {
                        sb.AppendFormat(" -> {0}", recipe.Result);
                    }
                }

                sb.Append(')');

                ImGui.Text(sb.ToString());
            }
        }

        private (HashSet<RecipeEntry> completedRecipes, HashSet<string> combinableSlottedIngredients) ProcessPutInIngredients(List<string> putInIngredients)
        {
            var completedRecipes = new HashSet<RecipeEntry>();
            putInIngredients = putInIngredients.ToList(); //will be modified
            for (int i = 1; i <= putInIngredients.Count; i++)
            {
                var completedRecipe = _recipesByName.Values.Except(completedRecipes).FirstOrDefault(x => !x.Recipe.Except(putInIngredients.Take(i)).Any());
                if (completedRecipe != null)
                {
                    putInIngredients.RemoveAll(completedRecipe.Recipe.Contains);
                    completedRecipes.Add(completedRecipe);
                    i = 0;
                }
            }

            return (completedRecipes, putInIngredients.ToHashSet());
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

            var fullPutInList = inputPanel.IsVisible
                                    ? _alreadyPutInCache = inputPanel.InventoryElements.Select(x => x.Item.DisplayName).ToList()
                                    : _alreadyPutInCache;
            var (completedRecipes, combinableIngredients) = ProcessPutInIngredients(fullPutInList);
            var excludedIngredients = fullPutInList.Except(combinableIngredients).ToHashSet();
            var presentIngredients =
                inventory.IsVisible
                    ? _presentIngredientCache =
                          _inventoryElementsCache.Value
                             .Select(x => x.itemDisplayName)
                             .Concat(fullPutInList)
                             .GroupBy(x => x)
                             .ToDictionary(x => x.Key, x => x.Count())
                    : _presentIngredientCache;
            if (windowState)
            {
                ImGui.Begin($"{Name}", ref windowState);
                ImGui.Text($"{4 - fullPutInList.Count} free slots left");
                var recipeToWorkOn = PickRecipeToWorkOn();
                var nextSteps = NextSteps.Empty;
                if (recipeToWorkOn != null)
                {
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
                    var recipesWithEnoughIngredients = GetRecipesWithEnoughIngredients(presentIngredients.Keys, excludedIngredients, fullPutInList);
                    recipesWithEnoughIngredients.UnionWith(completedRecipes);
                    var nextStepRecipeNames = nextSteps.RecipesToRun.ToHashSet();
                    if (_trackedRecipe != null && !recipesWithEnoughIngredients.Contains(_trackedRecipe))
                    {
                        RenderRecipeLine(_trackedRecipe, Settings.RecipeItemColor, completedRecipes, combinableIngredients);
                        ImGui.SameLine();
                        ImGui.TextColored(Color.Yellow.ToImguiVec4(), "(cannot complete in this map)");
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
                            RenderRecipeLine(recipe, Settings.RecipeItemColor, completedRecipes, combinableIngredients);
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
                            RenderRecipeLine(recipe, Settings.DesiredItemColor, completedRecipes, combinableIngredients);
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
                            RenderRecipeLine(recipe, Settings.UndesiredItemColor, completedRecipes, combinableIngredients);
                        }
                    }
                }

                ImGui.End();
            }

            //this window allows us to change the size of the text we draw to the background list
            //yeah, it's weird
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

                if (recipeSet.IsProperSupersetOf(presentIngredients.Keys))
                {
                    DrawText($"You are missing components of recipe {_trackedRecipe.Name}: " +
                             string.Join(", ", recipeSet.Except(presentIngredients.Keys)), textDisplayPosition, Color.Yellow);
                }
                else
                {
                    var itemMap = _trackedRecipe.Recipe.Except(combinableIngredients).Select((name, i) => (name, i))
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
                (x.element, x.itemDisplayName, Settings.GroundCrossThickness.Value, Settings.CacheGroundItemPosition.Value, true));
            if (inventory.IsVisible)
            {
                crossedElements = crossedElements.Concat(_inventoryElementsCache.Value.Select(x =>
                    ((Element)x.element, x.itemDisplayName, Settings.InventoryCrossThickness.Value, true, false)));
            }

            foreach (var (element, name, thickness, useCachePosition, drawCurrentCount) in crossedElements)
            {
                var elementRect = new Lazy<RectangleF>(() =>
                {
                    var rect = useCachePosition ? element.GetClientRectCache : element.GetClientRect();
                    rect.Inflate(-2, -2);
                    return rect;
                }, LazyThreadSafetyMode.None);
                if (!_desiredComponentCache.Contains(name) &&
                    (_trackedRecipe == null || !_trackedRecipe.Recipe.Contains(name)))
                {
                    drawList.AddLine(elementRect.Value.TopLeft.ToVector2Num(), elementRect.Value.BottomRight.ToVector2Num(), Settings.UndesiredItemColor.Value.ToImgui(),
                        thickness);
                    drawList.AddLine(elementRect.Value.BottomLeft.ToVector2Num(), elementRect.Value.TopRight.ToVector2Num(), Settings.UndesiredItemColor.Value.ToImgui(),
                        thickness);
                }
                else if (drawCurrentCount && presentIngredients.TryGetValue(name, out var value) && value > 0)
                {
                    drawList.AddText(elementRect.Value.TopLeft.ToVector2Num(), Settings.DesiredItemColor.Value.ToImgui(), value.ToString());
                }
            }

            ImGui.End();
        }

        private RecipeEntry PickRecipeToWorkOn()
        {
            var currentRecipeIndex = Math.Max(0,
                Settings.RecipeToWorkTowards.Values.IndexOf(Settings.RecipeToWorkTowards.Value ?? ArchnemesisRecipeTrackerSettings.NoRecipeSelected));
            ImGui.Text("Working towards");
            ImGui.SameLine();
            if (ImGui.Combo("##currentRecipe", ref currentRecipeIndex, Settings.RecipeToWorkTowards.Values.ToArray(), Settings.RecipeToWorkTowards.Values.Count))
            {
                Settings.RecipeToWorkTowards.Value = Settings.RecipeToWorkTowards.Values[currentRecipeIndex];
                _SaveSettings();
            }

            if (!_recipesByName.TryGetValue(Settings.RecipeToWorkTowards.Value ?? ArchnemesisRecipeTrackerSettings.NoRecipeSelected, out var recipeToWorkOn) &&
                Settings.RecipeToWorkTowards.Value != ArchnemesisRecipeTrackerSettings.NoRecipeSelected)
            {
                Settings.RecipeToWorkTowards.Value = ArchnemesisRecipeTrackerSettings.NoRecipeSelected;
            }

            return recipeToWorkOn;
        }
    }
}
