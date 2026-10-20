using System;
using HarmonyLib;
using UnityEngine;
using Verse;
using Waylith.Graphics;

namespace PortMobile
{
	/// <summary>
	/// Icons drawn without their material on ARM's Mali GPUs.
	///
	/// A thing whose graphic has a colour mask (beds and bedrolls, cribs, thrones, braziers, the blackboard, a toy box,
	/// an animal's body) gets a material for its icon: the map's shader for it has an interface twin
	/// (ShaderDatabase.TryGetUIShader, e.g. CutoutComplexUI), and Widgets.GetIconFor hands that material out with the
	/// texture. The engine draws such an icon through its interface renderer's material path
	/// (RimWorldWaylith.Imgui.ImGuiMaterialRenderer: a pipeline of its own with the scene's vertex shader and the
	/// material's uniforms and textures), every other icon as a plain texture. On Mali that path draws nothing or
	/// rubbish: the build menu's furniture shows empty squares or a diagonal streak where beds, bedrolls, the throne,
	/// braziers and the blackboard should be (two Dimensity 9200 phones, 1.7.4981 and 4982, few mods, plenty of
	/// graphics memory), and the animals' icons in the wildlife list flicker (Mali-G925, Mali-G57). Adreno draws them,
	/// with the textures decoded as on Mali too (bcDecode=1), so it is the GPU's handling of that path, which cannot be
	/// looked into from here.
	///
	/// Without the material the icon is the texture, tinted as every other icon (the stuff's colour, or the colour the
	/// material would have used): what the colour mask paints in a second colour (a bed's blanket) shows in the first.
	/// mobile.txt plainIcons=1 does this on any GPU (to see it on a phone that draws the materials), 0 never.
	/// </summary>
	internal static class PlainIcons
	{
		private const int ArmVendor = 0x13B5;

		public static void Start(Harmony harmony)
		{
			string setting = Settings.Get("plainIcons", "auto");
			bool arm = false;
			string gpu = "?";
			try
			{
				arm = GpuInfo.VendorId == ArmVendor;
				gpu = GpuInfo.DeviceName;
			}
			catch (Exception)
			{
			}
			bool on = setting == "1" || (setting != "0" && arm);
			if (!on)
			{
				Console.WriteLine("[mobile] icons: drawn with their materials (" + gpu + (setting == "0" ? ", plainIcons=0" : "") + ")");
				return;
			}
			try
			{
				harmony.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.GetIconFor), new[] { typeof(ThingDef), typeof(Material).MakeByRefType(), typeof(ThingDef), typeof(ThingStyleDef), typeof(int?) }),
					postfix: new HarmonyMethod(typeof(PlainIcons), nameof(AfterDefIcon)));
				harmony.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.GetIconFor), new[] { typeof(Thing), typeof(Vector2), typeof(Rot4?), typeof(bool), typeof(float).MakeByRefType(), typeof(float).MakeByRefType(), typeof(Vector2).MakeByRefType(), typeof(Color).MakeByRefType(), typeof(Material).MakeByRefType() }),
					postfix: new HarmonyMethod(typeof(PlainIcons), nameof(AfterThingIcon)));
				Console.WriteLine("[mobile] icons: drawn as plain textures, without the interface materials (" + gpu + (arm ? ", an ARM GPU" : ", plainIcons=1") + ")");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] icons: not changed: " + e.Message);
			}
		}

		/// <summary>Postfix of Widgets.GetIconFor(ThingDef, out Material, ...): ThingIcon then tints the texture as it does when there is no material.</summary>
		public static void AfterDefIcon(ref Material material)
		{
			material = null;
		}

		/// <summary>Postfix of Widgets.GetIconFor(Thing, ..., out Color, out Material): the colour it gives is the material's, which ThingIcon uses.</summary>
		public static void AfterThingIcon(ref Material material)
		{
			material = null;
		}
	}
}
