using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace PortMobile
{
	/// <summary>Settings from mobile.txt next to the game ("key=value" lines).</summary>
	internal static class Settings
	{
		private static readonly Dictionary<string, string> values = new Dictionary<string, string>();

		static Settings()
		{
			try
			{
				string path = System.IO.Path.Combine(AppContext.BaseDirectory, "mobile.txt");
				if (File.Exists(path))
				{
					foreach (string line in File.ReadAllLines(path))
					{
						int eq = line.IndexOf('=');
						if (eq > 0 && !line.StartsWith("#"))
						{
							values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
						}
					}
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] mobile.txt not read: " + e.Message);
			}
		}

		public static bool Get(string key, bool fallback)
		{
			return values.TryGetValue(key, out string v) ? v != "0" && !v.Equals("false", StringComparison.OrdinalIgnoreCase) : fallback;
		}

		public static string Get(string key, string fallback)
		{
			return values.TryGetValue(key, out string v) ? v : fallback;
		}

		public static int Get(string key, int fallback)
		{
			return values.TryGetValue(key, out string v) && int.TryParse(v, out int n) ? n : fallback;
		}

		/// <summary>A setting chosen in the game: kept, and written to mobile.txt (its line replaced or added; the rest of the file stays).</summary>
		public static void Set(string key, string value)
		{
			values[key] = value;
			try
			{
				string path = System.IO.Path.Combine(AppContext.BaseDirectory, "mobile.txt");
				var lines = new List<string>(File.Exists(path) ? File.ReadAllLines(path) : new string[0]);
				int at = lines.FindIndex(l => !l.StartsWith("#") && l.IndexOf('=') > 0 && l.Substring(0, l.IndexOf('=')).Trim() == key);
				if (at >= 0)
				{
					lines[at] = key + "=" + value;
				}
				else
				{
					lines.Add(key + "=" + value);
				}
				File.WriteAllLines(path, lines.ToArray());
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] mobile.txt not written: " + e.Message);
			}
		}

		/// <summary>Buildings and drags are placed by a confirm button rather than by the finger lifting.</summary>
		public static readonly bool PlaceConfirm = Get("placeConfirm", true);
	}

	/// <summary>
	/// The map tools (designators, targeters) under a finger. A press reaches them at once (the touch layer sends
	/// it like a mouse's), so what acts on the press is held back here until the finger lifts:
	///   placing a building   nothing happens; the outline moves to where the finger lifts (Placement), the
	///                        blueprint is put down by the confirm button
	///   picking a target     the choice is made where the finger lifts, so that a drag can still look around
	///   designating by drag  the drag marks its cells (DragConfirm); the confirm button designates them
	/// A drag with such a deferred press turns into a pan (Touch tells us: CancelDeferred).
	/// </summary>
	internal static class MapTools
	{
		private static bool deferred, deferredOnWorld, deferredIsPlacing, deferredCancelled, converted;
		private static bool deferredOnlyMoves;

		public static bool Deferred => deferred;

		public static void Start(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(MapInterface), nameof(MapInterface.HandleMapClicks)), prefix: new HarmonyMethod(typeof(MapTools), nameof(BeforeMapTools)), postfix: new HarmonyMethod(typeof(MapTools), nameof(AfterMapTools)));
			harmony.Patch(AccessTools.Method(typeof(WorldTargeter), nameof(WorldTargeter.ProcessInputEvents)), prefix: new HarmonyMethod(typeof(MapTools), nameof(BeforeWorldTargeter)), postfix: new HarmonyMethod(typeof(MapTools), nameof(AfterMapTools)));
			Placement.Start(harmony);
			WorldConfirm.Start(harmony);
			DragConfirm.Start(harmony);
		}

		/// <summary>Every frame (from Touch).</summary>
		public static void Update()
		{
			Placement.Update();
			DragConfirm.Update();
		}

		/// <summary>The finger that pressed is now panning the map (or a second finger joined): the held-back press is dropped.</summary>
		public static void CancelDeferred()
		{
			deferredCancelled = true;
			Placement.TouchEnded(true);
			DragConfirm.DragCancelled();
			if (Current.ProgramState == ProgramState.Playing && Find.DesignatorManager != null && Find.DesignatorManager.Dragger.Dragging)
			{
				Find.DesignatorManager.Dragger.EndDrag();
			}
		}

		/// <summary>Prefix of MapInterface.HandleMapClicks: before the designator and the targeter get the event.</summary>
		public static void BeforeMapTools()
		{
			Event current = Event.current;
			if (current == null || current.button != 0)
			{
				return;
			}
			if (current.type == EventType.MouseDown)
			{
				deferred = deferredOnWorld = false;
				if (Find.CurrentMap == null || !WorldRendererUtility.DrawingMap || !Touch.TouchInProgress || !Settings.PlaceConfirm)
				{
					return;
				}
				DesignatorManager tools = Find.DesignatorManager;
				bool placing = tools.SelectedDesignator != null && tools.SelectedStyle == null;
				bool targeting = tools.SelectedDesignator == null && Find.Targeter.IsTargeting;
				if (tools.SelectedDesignator != null && tools.SelectedStyle != null)
				{
					DragConfirm.DragStarted();
				}
				if (Touch.Verbose) Console.WriteLine("[mobile] map tools get a press: placing " + placing + ", targeting " + targeting + ", dragging " + (tools.SelectedDesignator != null && tools.SelectedStyle != null) + ", tool " + (tools.SelectedDesignator?.GetType().Name ?? "none"));
				if (placing || targeting)
				{
					// Both act the moment the button goes down, at a place the player could not see beforehand.
					deferred = true;
					deferredIsPlacing = placing;
					deferredCancelled = false;
					Designator_Place building = placing ? Placement.Tool() : null;
					deferredOnlyMoves = building != null;
					if (deferredOnlyMoves)
					{
						Placement.TouchBegan(building);
					}
					current.Use();
				}
			}
			else if (current.rawType == EventType.MouseUp && deferred && !deferredOnWorld)
			{
				deferred = false;
				if (deferredOnlyMoves)
				{
					deferredOnlyMoves = false;
					Placement.TouchEnded(deferredCancelled);
				}
				else if (!deferredCancelled && current.type == EventType.MouseUp)
				{
					// The choice is made now, where the finger lifted: the tool sees the press it was kept from.
					current.type = EventType.MouseDown;
					converted = true;
				}
			}
			else if (current.type == EventType.MouseUp && !deferred && Find.CurrentMap != null && DragConfirm.DragEnded())
			{
				// The drag's cells are marked, not designated: the game must not see the release.
				current.Use();
			}
		}

		public static void AfterMapTools()
		{
			if (converted)
			{
				converted = false;
				// Left over (the tool went away meanwhile): it must not start a selection box that no release ends.
				if (Event.current.type == EventType.MouseDown)
				{
					Event.current.Use();
				}
			}
		}

		/// <summary>The same for the world: picking a tile acts when the button goes down, so the first touch of a drag across the globe would be the choice.</summary>
		public static void BeforeWorldTargeter(WorldTargeter __instance)
		{
			Event current = Event.current;
			if (current == null || current.button != 0)
			{
				return;
			}
			if (current.type == EventType.MouseDown)
			{
				deferred = deferredOnWorld = false;
				if (!__instance.IsTargeting || !Touch.TouchInProgress || !Settings.PlaceConfirm)
				{
					return;
				}
				deferred = deferredOnWorld = true;
				deferredIsPlacing = false;
				deferredCancelled = false;
				current.Use();
			}
			else if (current.rawType == EventType.MouseUp && deferred && deferredOnWorld)
			{
				deferred = deferredOnWorld = false;
				if (!deferredCancelled && current.type == EventType.MouseUp && __instance.IsTargeting)
				{
					if (WorldConfirm.FirstTap(__instance))
					{
						current.Use();
						return;
					}
					current.type = EventType.MouseDown;
					converted = true;
				}
			}
		}
	}

	/// <summary>
	/// A target on the world is chosen by two taps on it (testers, 2026-10-20: a tap on the globe sent a shuttle off at
	/// once, an empty tile included). The first tap marks it (the marker stays on it, a message says to tap again); a
	/// second tap on the same tile or world object is the choice, a tap elsewhere marks that instead. With a mouse (no
	/// touch) the game's own single click. mobile.txt: worldConfirm=0 leaves it out.
	/// </summary>
	internal static class WorldConfirm
	{
		private static readonly bool On = Settings.Get("worldConfirm", true);
		private static readonly AccessTools.FieldRef<WorldTargeter, Func<GlobalTargetInfo, bool>> CanSelect = AccessTools.FieldRefAccess<WorldTargeter, Func<GlobalTargetInfo, bool>>("canSelectTarget");
		private static readonly Func<WorldTargeter, GlobalTargetInfo> UnderMouse = AccessTools.MethodDelegate<Func<WorldTargeter, GlobalTargetInfo>>(AccessTools.Method(typeof(WorldTargeter), "CurrentTargetUnderMouse"));
		private static readonly AccessTools.FieldRef<WorldTargeter, Func<GlobalTargetInfo, bool>> Action = AccessTools.FieldRefAccess<WorldTargeter, Func<GlobalTargetInfo, bool>>("action");
		private static GlobalTargetInfo marked = GlobalTargetInfo.Invalid;
		// The targeting the mark belongs to (a new one, begun without a stop the patches saw, starts unmarked).
		private static Func<GlobalTargetInfo, bool> markedFor;

		/// <summary>A tap (lifted) while the world targeter is on: true when it only marked the target (the event is not the targeter's).</summary>
		public static bool FirstTap(WorldTargeter targeter)
		{
			if (!On)
			{
				return false;
			}
			GlobalTargetInfo target = UnderMouse(targeter);
			Func<GlobalTargetInfo, bool> canSelect = CanSelect(targeter);
			if (!target.IsValid || (canSelect != null && !canSelect(target)))
			{
				// Not a target: the targeter does with the tap what it does (nothing, or says why not).
				return false;
			}
			if (marked.IsValid && marked == target && markedFor == Action(targeter))
			{
				Clear();
				return false;
			}
			marked = target;
			markedFor = Action(targeter);
			SoundDefOf.Tick_Low.PlayOneShotOnCamera();
			Messages.Message(Texts.Chinese ? "再点一次同一个地方确认目标（点别处换目标，返回键取消）" : "Tap the same place again to confirm the target (elsewhere to change it, Back to cancel)", MessageTypeDefOf.SilentInput, false);
			return true;
		}

		public static void Start(Harmony harmony)
		{
			if (!On)
			{
				return;
			}
			try
			{
				var clear = new HarmonyMethod(typeof(WorldConfirm), nameof(Clear));
				harmony.Patch(AccessTools.Method(typeof(WorldTargeter), nameof(WorldTargeter.BeginTargeting)), postfix: clear);
				harmony.Patch(AccessTools.Method(typeof(WorldTargeter), nameof(WorldTargeter.StopTargeting)), postfix: clear);
				harmony.Patch(AccessTools.Method(typeof(WorldTargeter), nameof(WorldTargeter.TargeterUpdate)), postfix: new HarmonyMethod(typeof(WorldConfirm), nameof(DrawMarked)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] world confirm: not patched: " + e.Message);
			}
		}

		public static void Clear()
		{
			marked = GlobalTargetInfo.Invalid;
			markedFor = null;
		}

		/// <summary>Postfix of WorldTargeter.TargeterUpdate: the marked target keeps its marker wherever the finger goes.</summary>
		public static void DrawMarked(WorldTargeter __instance)
		{
			if (!marked.IsValid || !__instance.IsTargeting || markedFor != Action(__instance))
			{
				return;
			}
			if (marked.HasWorldObject && marked.WorldObject.Destroyed)
			{
				marked = GlobalTargetInfo.Invalid;
				return;
			}
			Vector3 pos = marked.HasWorldObject ? marked.WorldObject.DrawPos : Find.WorldGrid.GetTileCenter(marked.Tile);
			WorldRendererUtility.DrawQuadTangentialToPlanet(pos, 0.8f * Find.WorldGrid.AverageTileSize, 0.05f, WorldMaterials.CurTargetingMat);
		}
	}

	/// <summary>
	/// Placing a building in two steps. With a mouse the building's outline follows the pointer and a click puts
	/// the blueprint down; on a touch screen every touch is a click, so a finger that only meant to look around
	/// left blueprints behind. Here a touch on the map only moves the outline (a tap puts it there, a drag
	/// carries it along), and the blueprint is placed by a "confirm" button next to the game's rotate buttons.
	///
	/// The outline is wherever the game thinks the mouse is. While a place is chosen, the game is told that the
	/// mouse is over that map cell, whatever the fingers do meanwhile (press buttons, move the camera).
	/// </summary>
	internal static class Placement
	{
		private static Designator_Place tool;
		private static bool chosen, following, hadChoice;
		private static IntVec3 cell, cellBefore;
		private static int controlsFrame = -10;
		private static bool lastResort, drew, complained;

		public static void Start(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(UI), nameof(UI.MouseCell)), prefix: new HarmonyMethod(typeof(Placement), nameof(MouseCell)));
			harmony.Patch(AccessTools.Method(typeof(Designator_Place), nameof(Designator_Place.DoExtraGuiControls)), prefix: new HarmonyMethod(typeof(Placement), nameof(KeepControlsOnScreen)), postfix: new HarmonyMethod(typeof(Placement), nameof(Controls)));
			harmony.Patch(AccessTools.Method(typeof(DesignatorManager), nameof(DesignatorManager.DesignationManagerOnGUI)), postfix: new HarmonyMethod(typeof(Placement), nameof(ControlsOfLastResort)));
		}

		/// <summary>The selected tool, if it is one that places a single building where the pointer is.</summary>
		public static Designator_Place Tool()
		{
			if (!Settings.PlaceConfirm || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || !WorldRendererUtility.DrawingMap)
			{
				return null;
			}
			DesignatorManager manager = Find.DesignatorManager;
			return manager.SelectedStyle == null ? manager.SelectedDesignator as Designator_Place : null;
		}

		/// <summary>Prefix of UI.MouseCell.</summary>
		public static bool MouseCell(ref IntVec3 __result)
		{
			if (chosen && !following)
			{
				__result = cell;
				return false;
			}
			return true;
		}

		public static void Update()
		{
			if (!chosen && !following && tool == null)
			{
				return;
			}
			if (Tool() != tool)
			{
				tool = null;
				chosen = following = false;
			}
		}

		/// <summary>A finger landed on the map with the tool active: the outline goes with it.</summary>
		public static void TouchBegan(Designator_Place selected)
		{
			if (selected != tool)
			{
				tool = selected;
				chosen = false;
			}
			hadChoice = chosen;
			cellBefore = cell;
			following = true;
		}

		/// <summary>The finger lifted (the outline stays there) or the touch became something else.</summary>
		public static void TouchEnded(bool cancelled)
		{
			if (!following)
			{
				return;
			}
			following = false;
			if (cancelled)
			{
				chosen = hadChoice;
				cell = cellBefore;
			}
			else
			{
				cell = UI.UIToMapPosition(UI.MousePositionOnUI).ToIntVec3();
				chosen = true;
			}
		}

		// The immediate window of an open inspect tab (InspectTabBase.DoTabGUI), as the window stack numbers it.
		private const int InspectTabWindowId = -235086;

		/// <summary>
		/// Prefix of DoExtraGuiControls (Designator_Place's and the base one). The game puts a tool's extra controls (rotate buttons, drawing styles) in a
		/// small window above the description box; when that is above the top of the screen (a tall Architect menu
		/// at a large UI scale) they go to the right of the description box instead, and to the right of an open
		/// inspect tab that would cover them.
		/// </summary>
		public static void KeepControlsOnScreen(ref float leftX, ref float bottomY)
		{
			if (bottomY - 90f < 0f)
			{
				leftX += 208f;
				bottomY = Mathf.Max(0f, bottomY) + 90f;
			}
			var controls = new Rect(leftX, bottomY - 90f, 400f, 90f);
			IList<Window> windows = Find.WindowStack.Windows;
			for (int i = 0; i < windows.Count; i++)
			{
				Window window = windows[i];
				if (window.ID == InspectTabWindowId && window is ImmediateWindow && window.windowRect.Overlaps(controls))
				{
					leftX = window.windowRect.xMax + 8f;
					break;
				}
			}
		}

		/// <summary>A tool's extra controls were drawn by the game this frame.</summary>
		internal static void NoteControls()
		{
			if (!lastResort)
			{
				controlsFrame = Time.frameCount;
			}
		}

		internal static void NoteDrew()
		{
			drew = true;
		}

		private static bool Rotates(Designator_Place selected)
		{
			return selected.PlacingDef is ThingDef def && def.rotatable;
		}

		/// <summary>Postfix of Designator_Place.DoExtraGuiControls: the buttons, right of the rotate buttons.</summary>
		public static void Controls(Designator_Place __instance, float leftX, float bottomY)
		{
			NoteControls();
			if (Tool() == __instance)
			{
				Draw(ButtonsRect(__instance, leftX, bottomY));
			}
			else if (Rotates(__instance) && DragConfirm.Tool() == __instance)
			{
				DragConfirm.Draw(DragConfirm.ButtonsRect(__instance, leftX, bottomY, true));
			}
		}

		private static Rect ButtonsRect(Designator_Place selected, float leftX, float bottomY)
		{
			return new Rect(leftX + (Rotates(selected) ? 200f : 0f), bottomY - 90f, 200f, 90f);
		}

		/// <summary>
		/// Postfix of DesignatorManager.DesignationManagerOnGUI: the tool's controls when the game does not show them: the
		/// Architect menu closed with the tool still selected, or open with no category open (the game draws them only
		/// from the open category; tapping the category again closes it and leaves the tool selected).
		/// </summary>
		public static void ControlsOfLastResort()
		{
			if (controlsFrame >= Time.frameCount - 2 || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || !WorldRendererUtility.DrawingMap)
			{
				return;
			}
			Designator selected = Find.DesignatorManager.SelectedDesignator;
			if (selected == null)
			{
				return;
			}
			MainButtonDef openTab = Find.MainTabsRoot.OpenTab;
			bool menuOpen = openTab == MainButtonDefOf.Architect;
			bool dragging = DragConfirm.Tool() == selected;
			bool ownDrag = DragConfirm.OwnDragTool() == selected;
			if (!(selected is Designator_Place) && !dragging && !ownDrag)
			{
				return;
			}
			// With the menu open its categories cover the bottom left: the controls go above them, as the game puts them.
			// The inspect pane (things selected, as Select similar leaves them) is there too: above it.
			float bottomY = menuOpen ? UI.screenHeight - 35 - ((MainTabWindow_Architect)MainButtonDefOf.Architect.TabWindow).WinHeight - 270f
				: openTab == MainButtonDefOf.Inspect ? MainButtonDefOf.Inspect.TabWindow.windowRect.y - 8f
				: UI.screenHeight - 35 - 10;
			lastResort = true;
			drew = false;
			try
			{
				selected.DoExtraGuiControls(0f, bottomY);
			}
			catch (Exception e)
			{
				if (!complained)
				{
					complained = true;
					Log.Error("PortMobile tool controls failed: " + e);
				}
			}
			finally
			{
				lastResort = false;
			}
			if (drew)
			{
				return;
			}
			if (Tool() == selected)
			{
				Draw(ButtonsRect((Designator_Place)selected, 0f, bottomY));
			}
			else if (dragging)
			{
				DragConfirm.Draw(DragConfirm.ButtonsRect(selected, 0f, bottomY, selected is Designator_Place place && Rotates(place)));
			}
			else
			{
				DragConfirm.DrawPutAway(DragConfirm.ButtonsRect(selected, 0f, bottomY, false));
			}
		}

		private static void Draw(Rect window)
		{
			NoteDrew();
			Find.WindowStack.ImmediateWindow(73195731, window, WindowLayer.GameUI, delegate
			{
				Text.Font = GameFont.Small;
				var inner = new Rect(0f, 0f, window.width, window.height).ContractedBy(10f);
				float width = (inner.width - 10f) / 2f;
				if (Widgets.ButtonText(new Rect(inner.x, inner.y, width, inner.height), "Confirm".Translate()))
				{
					Confirm();
				}
				if (Widgets.ButtonText(new Rect(inner.xMax - width, inner.y, width, inner.height), "CancelButton".Translate()))
				{
					SoundDefOf.CancelMode.PlayOneShotOnCamera();
					Find.DesignatorManager.Deselect();
				}
			});
		}

		private static void Confirm()
		{
			Designator_Place selected = Tool();
			if (selected == null)
			{
				return;
			}
			if (Touch.Verbose) Console.WriteLine("[mobile] confirm placement: chosen " + chosen + ", same tool " + (selected == tool) + ", cell " + cell);
			if (!chosen || selected != tool)
			{
				Messages.Message(Texts.Chinese ? "先在地图上点一下要放的位置。" : "Tap the map where it should go first.", MessageTypeDefOf.RejectInput, false);
				return;
			}
			// What the game does for a click at that place.
			AcceptanceReport report = selected.CanDesignateCell(cell);
			if (report.Accepted)
			{
				selected.DesignateSingleCell(cell);
				selected.Finalize(true);
			}
			else
			{
				Messages.Message(report.Reason, MessageTypeDefOf.SilentInput, false);
				selected.Finalize(false);
			}
		}
	}

	/// <summary>
	/// Designating by dragging (mining, cancelling, cutting plants, zones, walls, floors and every other tool with a
	/// drawing style) in two steps, like placing a building. A drag only marks its cells: they stay highlighted,
	/// further drags add to them, and a "confirm" button next to the tool's own controls designates them all at
	/// once. "Cancel" drops the marked cells, and with none marked it puts the tool away. With a single-cell
	/// drawing style a tap on a marked cell unmarks it.
	/// </summary>
	internal static class DragConfirm
	{
		private static readonly AccessTools.FieldRef<DesignationDragger, IntVec3> StartCell = AccessTools.FieldRefAccess<DesignationDragger, IntVec3>("startDragCell");
		private static Texture2D outlineTex;
		private static Designator tool;
		private static Map map;
		private static readonly List<IntVec3> cells = new List<IntVec3>();
		private static readonly HashSet<IntVec3> cellSet = new HashSet<IntVec3>();
		private static readonly List<CellRect> outlines = new List<CellRect>();
		private static readonly List<IntVec3> visible = new List<IntVec3>();
		private static bool cancelled;

		public static void Start(Harmony harmony)
		{
			// The controls of every tool are kept on the screen, not only a building's: a tall Architect menu (many mods' categories) put them above its top.
			harmony.Patch(AccessTools.Method(typeof(Designator), nameof(Designator.DoExtraGuiControls)), prefix: new HarmonyMethod(typeof(Placement), nameof(Placement.KeepControlsOnScreen)), postfix: new HarmonyMethod(typeof(DragConfirm), nameof(Controls)));
			harmony.Patch(AccessTools.Method(typeof(DesignatorManager), nameof(DesignatorManager.DesignatorManagerUpdate)), postfix: new HarmonyMethod(typeof(DragConfirm), nameof(AfterUpdate)));
			harmony.Patch(AccessTools.Method(typeof(DesignatorManager), nameof(DesignatorManager.DesignationManagerOnGUI)), postfix: new HarmonyMethod(typeof(DragConfirm), nameof(OnGUI)));
		}

		/// <summary>The selected tool, if it designates the cells of a drag (or of single taps with a drawing style).</summary>
		public static Designator Tool()
		{
			if (!Settings.PlaceConfirm || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || !WorldRendererUtility.DrawingMap)
			{
				return null;
			}
			DesignatorManager manager = Find.DesignatorManager;
			return manager.SelectedStyle != null && !KeepsOwnDrag(manager.SelectedDesignator) ? manager.SelectedDesignator : null;
		}

		/// <summary>
		/// The selected tool, if it acts by itself when the finger lifts (KeepsOwnDrag): it gets only a cancel button, the
		/// mouse's right click (the user, 2026-10-22: after "选择相似" there was no quick way to put the tool away).
		/// </summary>
		public static Designator OwnDragTool()
		{
			if (!Settings.PlaceConfirm || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || !WorldRendererUtility.DrawingMap)
			{
				return null;
			}
			Designator selected = Find.DesignatorManager.SelectedDesignator;
			return KeepsOwnDrag(selected) ? selected : null;
		}

		private static readonly Dictionary<Type, bool> ownDrag = new Dictionary<Type, bool>();

		/// <summary>
		/// A tool that follows the drag itself and designates from that, not from the cells it is handed: Allow Tool's
		/// (on its Designator_UnlimitedDragger: Select similar, Allow / Forbid all, Haul urgently, ...). By the time a
		/// confirmation hands it the marked cells its own area is gone, and nothing was selected (the user, 2026-10-22: a
		/// tree, "选择相似", a box around other trees). These act when the finger lifts, as with a mouse.
		/// </summary>
		private static bool KeepsOwnDrag(Designator designator)
		{
			if (designator == null)
			{
				return false;
			}
			Type type = designator.GetType();
			if (!ownDrag.TryGetValue(type, out bool own))
			{
				for (Type t = type; t != null && t != typeof(Designator) && !own; t = t.BaseType)
				{
					own = t.Name == "Designator_UnlimitedDragger";
				}
				ownDrag[type] = own;
			}
			return own;
		}

		/// <summary>Every frame: marked cells belong to one tool on one map.</summary>
		public static void Update()
		{
			if (tool != null && (Tool() != tool || Find.CurrentMap != map))
			{
				Clear();
			}
		}

		private static void Clear()
		{
			tool = null;
			map = null;
			cells.Clear();
			cellSet.Clear();
			outlines.Clear();
		}

		internal static void DragStarted()
		{
			cancelled = false;
		}

		internal static void DragCancelled()
		{
			cancelled = true;
		}

		/// <summary>The finger lifted. True when the drag's cells were taken here, so that the game must not designate them itself.</summary>
		internal static bool DragEnded()
		{
			Designator selected = Tool();
			if (selected == null)
			{
				return false;
			}
			DesignatorManager manager = Find.DesignatorManager;
			DesignationDragger dragger = manager.Dragger;
			bool single = manager.SelectedStyle.DrawStyleWorker.SingleCell;
			if (!dragger.Dragging && !single)
			{
				return false;
			}
			if (cancelled)
			{
				dragger.EndDrag();
				return true;
			}
			if (selected != tool || Find.CurrentMap != map)
			{
				Clear();
				tool = selected;
				map = Find.CurrentMap;
			}
			List<IntVec3> dragged = dragger.DragCells;
			if (dragged.Count > 0)
			{
				if (single && dragged.TrueForAll(cellSet.Contains))
				{
					foreach (IntVec3 c in dragged)
					{
						cellSet.Remove(c);
						cells.Remove(c);
					}
				}
				else
				{
					foreach (IntVec3 c in dragged)
					{
						if (cellSet.Add(c))
						{
							cells.Add(c);
						}
					}
					if (!single && manager.SelectedStyle.drawOutline)
					{
						IntVec3 start = StartCell(dragger);
						IntVec3 end = UI.MouseCell();
						outlines.Add(CellRect.FromLimits(Math.Min(start.x, end.x), Math.Min(start.z, end.z), Math.Max(start.x, end.x), Math.Max(start.z, end.z)));
					}
				}
			}
			dragger.EndDrag();
			return true;
		}

		/// <summary>Postfix of DesignatorManager.DesignatorManagerUpdate: the marked cells, drawn as the tool draws a drag.</summary>
		public static void AfterUpdate()
		{
			if (cells.Count == 0 || Tool() != tool || !tool.DrawHighlight)
			{
				return;
			}
			CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(3).ClipInsideMap(map);
			visible.Clear();
			foreach (IntVec3 c in cells)
			{
				if (view.Contains(c))
				{
					visible.Add(c);
				}
			}
			if (visible.Count > 0)
			{
				tool.RenderHighlight(visible);
			}
		}

		/// <summary>Postfix of DesignatorManager.DesignationManagerOnGUI: the outline of each drag, as during the drag.</summary>
		public static void OnGUI()
		{
			if (outlines.Count == 0 || Event.current.type != EventType.Repaint || Tool() != tool)
			{
				return;
			}
			if (outlineTex == null)
			{
				outlineTex = SolidColorMaterials.NewSolidColorTexture(new Color32(109, 139, 79, 100));
			}
			foreach (CellRect box in outlines)
			{
				if (box.Width <= 1 && box.Height <= 1)
				{
					continue;
				}
				Vector2 min = new Vector3(box.minX, 0f, box.minZ).MapToUIPosition();
				Vector2 max = new Vector3(box.maxX + 1, 0f, box.maxZ + 1).MapToUIPosition();
				Widgets.DrawBox(Rect.MinMaxRect(min.x, min.y, max.x, max.y), 1, outlineTex);
			}
		}

		/// <summary>Postfix of Designator.DoExtraGuiControls: the buttons, right of the drawing style picker when the game shows one.</summary>
		public static void Controls(Designator __instance, float leftX, float bottomY)
		{
			Placement.NoteControls();
			if (OwnDragTool() == __instance)
			{
				DrawPutAway(ButtonsRect(__instance, leftX, bottomY, false));
				return;
			}
			if (Tool() != __instance)
			{
				return;
			}
			Draw(ButtonsRect(__instance, leftX, bottomY, false));
		}

		internal static Rect ButtonsRect(Designator selected, float leftX, float bottomY, bool rotates)
		{
			List<DrawStyleDef> styles = selected.DrawStyleCategory?.styles;
			bool picker = !rotates && Find.DesignatorManager.SelectedStyle != null && styles != null && styles.Count > 1;
			return new Rect(leftX + (rotates || picker ? 200f : 0f), bottomY - 90f, 200f, 90f);
		}

		internal static void Draw(Rect window)
		{
			Placement.NoteDrew();
			Find.WindowStack.ImmediateWindow(73195732, window, WindowLayer.GameUI, delegate
			{
				Text.Font = GameFont.Small;
				var inner = new Rect(0f, 0f, window.width, window.height).ContractedBy(10f);
				float width = (inner.width - 10f) / 2f;
				if (Widgets.ButtonText(new Rect(inner.x, inner.y, width, inner.height), "Confirm".Translate()))
				{
					Confirm();
				}
				if (Widgets.ButtonText(new Rect(inner.xMax - width, inner.y, width, inner.height), "CancelButton".Translate()))
				{
					Cancel();
				}
			});
		}

		/// <summary>The cancel button alone, in the left half of the buttons' place.</summary>
		internal static void DrawPutAway(Rect window)
		{
			Placement.NoteDrew();
			window.width = window.width / 2f + 5f;
			Find.WindowStack.ImmediateWindow(73195733, window, WindowLayer.GameUI, delegate
			{
				Text.Font = GameFont.Small;
				if (Widgets.ButtonText(new Rect(0f, 0f, window.width, window.height).ContractedBy(10f), "CancelButton".Translate()))
				{
					SoundDefOf.CancelMode.PlayOneShotOnCamera();
					Find.DesignatorManager.Deselect();
				}
			});
		}

		private static void Confirm()
		{
			Designator selected = Tool();
			if (selected == null)
			{
				return;
			}
			if (cells.Count == 0 || selected != tool || Find.CurrentMap != map)
			{
				Messages.Message(Texts.Chinese ? "先在地图上拖出要指定的范围。" : "Drag over the area on the map first.", MessageTypeDefOf.RejectInput, false);
				return;
			}
			var marked = new List<IntVec3>(cells);
			Clear();
			// What the game does when the mouse button is released after a drag.
			selected.DesignateMultiCell(marked);
		}

		private static void Cancel()
		{
			if (cells.Count > 0)
			{
				SoundDefOf.Tick_Low.PlayOneShotOnCamera();
				Clear();
				return;
			}
			SoundDefOf.CancelMode.PlayOneShotOnCamera();
			Find.DesignatorManager.Deselect();
		}
	}

	internal static class Texts
	{
		public static bool Chinese => LanguageDatabase.activeLanguage != null && LanguageDatabase.activeLanguage.folderName.StartsWith("Chinese");
	}
}
