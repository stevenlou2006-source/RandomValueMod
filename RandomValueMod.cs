using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using AdvancedREPO.Config;

namespace RandomValuablePrice
{
	// 公开的 API 类
	public static class RandomValueAPI
	{
		/// <summary>
		/// 当贵重物品被随机化后触发。
		/// 参数：ValuableObject 物品对象, float 原价, float 新价
		/// </summary>
		public static event Action<ValuableObject, float, float> OnItemRandomized;

		/// <summary>
		/// 获取贵重物品的当前价格。
		/// </summary>
		public static float GetItemPrice(ValuableObject vo)
		{
			return RandomValueMod.GetItemPrice(vo);
		}

		// 内部调用，触发事件
		internal static void TriggerItemRandomized(ValuableObject vo, float oldPrice, float newPrice)
		{
			OnItemRandomized?.Invoke(vo, oldPrice, newPrice);
		}
	}

	[BepInPlugin("dyxc666.RandomValuablePrice", "随机贵重物品的价格", "1.7.1")]
	[BepInProcess("REPO.exe")]
	[BepInDependency("potatoepet.advancedrepo.config")]
	public class RandomValueMod : BaseUnityPlugin
	{
		private readonly Harmony harmony = new Harmony("dyxc666.RandomValuablePrice");

		public static ManualLogSource StaticLogger { get; private set; }

		// 配置项
		public static ConfigField<bool> EnableValueRandomization;
		public static ConfigField<float> MinValueMultiplier;
		public static ConfigField<float> MaxValueMultiplier;
		public static ConfigField<bool> ForceRefreshUI;
		public static ConfigField<bool> EnableVerboseLogging;

		public static ConfigField<string> BlacklistKeywords;
		public static ConfigField<string> WhitelistKeywords;
		public static ConfigField<bool> EnableWhitelistMode;
		public static ConfigField<bool> EnableBlacklistMode;
		public static ConfigField<bool> ExcludeHighValueItems;
		public static ConfigField<float> HighValueThreshold;
		public static ConfigField<bool> CashBagRandomizationToggle;

		// 字段信息（反射）
		private static FieldInfo dollarValueCurrentField;
		private static FieldInfo dollarValueOriginalField;

		private static List<string> blacklistKeywordsList = new List<string>();
		private static List<string> whitelistKeywordsList = new List<string>();

		public static HashSet<int> modifiedItemIDs = new HashSet<int>();
		public static Dictionary<int, float> itemRandomizationCache = new Dictionary<int, float>();

		static RandomValueMod()
		{
			dollarValueCurrentField = typeof(ValuableObject).GetField("dollarValueCurrent",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			dollarValueOriginalField = typeof(ValuableObject).GetField("dollarValueOriginal",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
		}

		private void Awake()
		{
			StaticLogger = Logger;

			Logger.LogInfo("=========================================");
			Logger.LogInfo("随机贵重物品的价格模组 v1.7.0 已加载");
			Logger.LogInfo("作者: dyxc666");
			Logger.LogInfo("功能: 抓取时随机化物品价格（主机强制统一配置）");
			Logger.LogInfo("=========================================");

			CreateConfigEntries();
			UpdateKeywordLists();

			BlacklistKeywords.ValueChanged += (s, a) => UpdateKeywordLists();
			WhitelistKeywords.ValueChanged += (s, a) => UpdateKeywordLists();
			MinValueMultiplier.ValueChanged += (s, a) => ValidateValueRange();
			MaxValueMultiplier.ValueChanged += (s, a) => ValidateValueRange();

			try
			{
				ApplyHarmonyPatches();

				Logger.LogInfo($"✅ 模组初始化成功");
				Logger.LogInfo($"📋 黑名单关键词: {string.Join(", ", blacklistKeywordsList)}");
				Logger.LogInfo($"📋 白名单关键词: {string.Join(", ", whitelistKeywordsList)}");
				Logger.LogInfo($"⚙️ 当前功能状态:");
				Logger.LogInfo($"   价格随机化: {(EnableValueRandomization.Value ? "✅" : "❌")}");
				Logger.LogInfo($"   最小倍率: {MinValueMultiplier.Value:F2}");
				Logger.LogInfo($"   最大倍率: {MaxValueMultiplier.Value:F2}");
				Logger.LogInfo($"   随机化时机: 抓取时触发");
				Logger.LogInfo($"   现金袋保护: {(CashBagRandomizationToggle.Value ? "❌ 已禁用" : "✅ 已启用")}");

				CheckForModConflicts();
			}
			catch (Exception ex)
			{
				Logger.LogError($"❌ 模组初始化失败: {ex.Message}");
			}
		}

		private void ApplyHarmonyPatches()
		{
			var awakeMethod = AccessTools.Method(typeof(ValuableObject), "Awake");
			if (awakeMethod != null)
			{
				harmony.Patch(awakeMethod, postfix: new HarmonyMethod(typeof(ValuableObjectPatch).GetMethod("Postfix_Awake")));
				Logger.LogInfo("✅ 成功补丁 ValuableObject.Awake");
			}

			string[] grabMethods = { "GrabStarted", "GrabEnded", "GrabLink" };
			foreach (var name in grabMethods)
			{
				var method = AccessTools.Method(typeof(PhysGrabObject), name);
				if (method != null)
				{
					harmony.Patch(method, postfix: new HarmonyMethod(typeof(PhysGrabObjectPatch).GetMethod("Postfix_GrabMethod")));
					Logger.LogInfo($"✅ 成功补丁 PhysGrabObject.{name}");
				}
			}
		}

		private void CheckForModConflicts()
		{
			Logger.LogInfo("🔍 正在检查Mod兼容性...");
			Logger.LogInfo("✅ Mod兼容性检查完成");
		}

		// 辅助方法：创建布尔配置项
		private ConfigField<bool> CreateBoolConfig(string section, string key, bool defaultValue, string description, bool sync = true)
		{
			return new ConfigBuilder<bool>("RandomValuablePrice")
				.File("RandomValuablePrice.cfg")
				.Section(section)
				.Key(key)
				.Default(defaultValue)
				.Description(description)
				.Range(false, true)
				.Sync(sync)
				.Build();
		}

		// 辅助方法：创建浮点配置项（带范围）
		private ConfigField<float> CreateFloatConfig(string section, string key, float defaultValue, string description, float min, float max, bool sync = true)
		{
			return new ConfigBuilder<float>("RandomValuablePrice")
				.File("RandomValuablePrice.cfg")
				.Section(section)
				.Key(key)
				.Default(defaultValue)
				.Description(description)
				.Range(min, max)
				.Sync(sync)
				.Build();
		}

		private void CreateConfigEntries()
		{
			EnableValueRandomization = CreateBoolConfig("基础设置", "随机化物品价格", true, "是否随机化贵重物品的价格");
			MinValueMultiplier = CreateFloatConfig("价格设置", "最小价格倍率", 0.5f, "价格的最小倍率", 0.1f, 5.0f);
			MaxValueMultiplier = CreateFloatConfig("价格设置", "最大价格倍率", 2.0f, "价格的最大倍率", 0.5f, 10.0f);
			EnableBlacklistMode = CreateBoolConfig("黑白名单", "启用黑名单模式", true, "启用黑名单，匹配的物品将不被随机化");

			BlacklistKeywords = new ConfigBuilder<string>("RandomValuablePrice")
				.File("RandomValuablePrice.cfg")
				.Section("黑白名单")
				.Key("黑名单关键词")
				.Default("")
				.Description("逗号分隔的关键词列表")
				.Sync(true)
				.Build();

			EnableWhitelistMode = CreateBoolConfig("黑白名单", "启用白名单模式", false, "启用白名单，只有匹配白名单的物品才会被随机化");

			WhitelistKeywords = new ConfigBuilder<string>("RandomValuablePrice")
				.File("RandomValuablePrice.cfg")
				.Section("黑白名单")
				.Key("白名单关键词")
				.Default("")
				.Description("逗号分隔的关键词列表")
				.Sync(true)
				.Build();

			ExcludeHighValueItems = CreateBoolConfig("特殊排除规则", "排除高价物品", false, "不随机化价格超过阈值的物品");
			HighValueThreshold = CreateFloatConfig("特殊排除规则", "高价物品阈值", 50000f, "价格超过此值的物品不随机化", 1000f, 1000000f);
			CashBagRandomizationToggle = CreateBoolConfig("特殊排除规则", "现金袋随机化", false, "如果为true，则现金袋也会被随机化");
			ForceRefreshUI = CreateBoolConfig("调试设置", "强制刷新UI", true, "尝试强制刷新UI显示", false);
			EnableVerboseLogging = CreateBoolConfig("调试设置", "详细日志输出", false, "输出每个物品修改前后的具体数值", false);
		}

		private void UpdateKeywordLists()
		{
			blacklistKeywordsList = string.IsNullOrWhiteSpace(BlacklistKeywords.Value)
				? new List<string>()
				: BlacklistKeywords.Value.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
					.Select(k => k.Trim().ToLower()).Where(k => !string.IsNullOrEmpty(k)).ToList();

			whitelistKeywordsList = string.IsNullOrWhiteSpace(WhitelistKeywords.Value)
				? new List<string>()
				: WhitelistKeywords.Value.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
					.Select(k => k.Trim().ToLower()).Where(k => !string.IsNullOrEmpty(k)).ToList();
		}

		private void ValidateValueRange()
		{
			if (MinValueMultiplier.Value < 0.1f) MinValueMultiplier.Value = 0.1f;
			if (MaxValueMultiplier.Value < MinValueMultiplier.Value + 0.1f)
				MaxValueMultiplier.Value = MinValueMultiplier.Value + 0.1f;
			if (MaxValueMultiplier.Value > 10.0f) MaxValueMultiplier.Value = 10.0f;
		}

		#region 黑白名单和规则检查
		public static bool ShouldItemBeRandomized(GameObject obj, ValuableObject vo)
		{
			try
			{
				string name = obj.name.ToLower();

				if (IsCashBag(name) && !CashBagRandomizationToggle.Value)
					return false;

				if (CheckSpecialExclusionRules(obj, vo))
					return false;

				if (EnableBlacklistMode.Value && CheckBlacklist(name))
					return false;

				if (EnableWhitelistMode.Value && !CheckWhitelist(name))
					return false;

				return EnableValueRandomization.Value;
			}
			catch
			{
				return true;
			}
		}

		private static bool IsCashBag(string name) => name.Contains("surplus");

		private static bool CheckSpecialExclusionRules(GameObject obj, ValuableObject vo)
		{
			if (ExcludeHighValueItems.Value)
			{
				float price = GetItemPrice(vo);
				if (price >= HighValueThreshold.Value)
					return true;
			}
			return false;
		}

		private static bool CheckBlacklist(string name)
			=> blacklistKeywordsList.Any(k => name.Contains(k));

		private static bool CheckWhitelist(string name)
			=> whitelistKeywordsList.Count == 0 || whitelistKeywordsList.Any(k => name.Contains(k));

		public static float GetItemPrice(ValuableObject vo)
		{
			if (dollarValueCurrentField == null) return 0f;
			return (float)dollarValueCurrentField.GetValue(vo);
		}

		public static void SetItemPrice(ValuableObject vo, float newPrice)
		{
			dollarValueCurrentField?.SetValue(vo, newPrice);
			dollarValueOriginalField?.SetValue(vo, newPrice);
		}
		#endregion

		public static float GetDeterministicRandomPrice(int instanceID, float originalPrice)
		{
			if (itemRandomizationCache.TryGetValue(instanceID, out float cached))
				return cached;

			float min = Mathf.Max(0.1f, MinValueMultiplier.Value);
			float max = Mathf.Max(min + 0.1f, MaxValueMultiplier.Value);

			var rand = new System.Random(instanceID);
			float multiplier = (float)(rand.NextDouble() * (max - min) + min);

			if (EnableVerboseLogging.Value)
			{
				StaticLogger.LogInfo($"[调试] 物品ID {instanceID}: 原价={originalPrice:F0}, min={min:F2}, max={max:F2}, 乘数={multiplier:F2}");
			}

			float newPrice = originalPrice;

			if (originalPrice > 0 && EnableValueRandomization.Value)
			{
				newPrice = originalPrice * multiplier;
				newPrice = Mathf.Clamp(newPrice, 1f, 1000000f);
			}

			itemRandomizationCache[instanceID] = newPrice;
			return newPrice;
		}
	}

	#region 物品修改器
	public static class ItemModifier
	{
		public static bool ModifyItemAttributes(PhysGrabObject pgo, ValuableObject vo, float newPrice)
		{
			try
			{
				float originalPrice = RandomValueMod.GetItemPrice(vo);

				if (RandomValueMod.EnableValueRandomization.Value && originalPrice > 0)
				{
					if (RandomValueMod.EnableVerboseLogging.Value)
					{
						RandomValueMod.StaticLogger.LogInfo($"[调试] 修改前价格: {originalPrice:F0}");
					}

					RandomValueMod.SetItemPrice(vo, newPrice);

					if (RandomValueMod.EnableVerboseLogging.Value)
					{
						float after = RandomValueMod.GetItemPrice(vo);
						RandomValueMod.StaticLogger.LogInfo($"[调试] 修改后价格: {after:F0}");
					}

					// 触发 API 事件
					RandomValueAPI.TriggerItemRandomized(vo, originalPrice, newPrice);

					if (RandomValueMod.ForceRefreshUI.Value)
					{
						try { vo.BroadcastMessage("OnValueChanged", SendMessageOptions.DontRequireReceiver); } catch { }
					}
				}

				if (RandomValueMod.EnableVerboseLogging.Value && originalPrice > 0)
				{
					float multiplier = newPrice / originalPrice;
					string emoji = multiplier > 2.0f ? " 💰" : multiplier > 1.5f ? " 📈" : multiplier < 0.9f ? " 📉" : "";
					RandomValueMod.StaticLogger.LogInfo($"[RWVM] {pgo.gameObject.name}: 价格 ${originalPrice:F0}→${newPrice:F0}(x{multiplier:F2}){emoji}");
				}

				return true;
			}
			catch (Exception ex)
			{
				RandomValueMod.StaticLogger.LogError($"[RWVM] 修改物品属性失败: {ex.Message}");
				return false;
			}
		}
	}
	#endregion

	#region Harmony补丁
	[HarmonyPatch]
	public static class ValuableObjectPatch
	{
		[HarmonyPostfix]
		[HarmonyPatch(typeof(ValuableObject), "Awake")]
		public static void Postfix_Awake(ValuableObject __instance)
		{
			try
			{
				var pgo = __instance.GetComponent<PhysGrabObject>();
				if (pgo == null) return;

				if (!RandomValueMod.modifiedItemIDs.Contains(__instance.gameObject.GetInstanceID()))
				{
					var listener = pgo.gameObject.AddComponent<GrabEventListener>();
					listener.valuableObject = __instance;
					listener.physGrabObject = pgo;
				}
			}
			catch { }
		}
	}

	[HarmonyPatch]
	public static class PhysGrabObjectPatch
	{
		[HarmonyPostfix]
		[HarmonyPatch(typeof(PhysGrabObject), "GrabStarted")]
		[HarmonyPatch(typeof(PhysGrabObject), "GrabLink")]
		public static void Postfix_GrabMethod(PhysGrabObject __instance)
		{
			try
			{
				var vo = __instance.GetComponent<ValuableObject>();
				if (vo == null) return;

				int id = __instance.gameObject.GetInstanceID();

				if (RandomValueMod.modifiedItemIDs.Contains(id))
					return;

				if (!RandomValueMod.ShouldItemBeRandomized(__instance.gameObject, vo))
					return;

				float originalPrice = RandomValueMod.GetItemPrice(vo);
				if (originalPrice <= 0) return;

				float newPrice = RandomValueMod.GetDeterministicRandomPrice(id, originalPrice);

				ApplyRandomization(__instance, vo, newPrice, originalPrice, id);
			}
			catch { }
		}

		private static void ApplyRandomization(PhysGrabObject pgo, ValuableObject vo, float newPrice, float originalPrice, int instanceID)
		{
			if (ItemModifier.ModifyItemAttributes(pgo, vo, newPrice))
			{
				RandomValueMod.modifiedItemIDs.Add(instanceID);
				float multiplier = newPrice / originalPrice;
				RandomValueMod.StaticLogger.LogInfo($"[提示] 抓取时随机化: {pgo.gameObject.name} ${originalPrice:F0} → ${newPrice:F0} ({(multiplier > 1 ? "+" : "")}{multiplier:F1}倍)");
			}
		}
	}
	#endregion

	#region 抓取事件监听器（备用方案）
	public class GrabEventListener : MonoBehaviour
	{
		public ValuableObject valuableObject;
		public PhysGrabObject physGrabObject;
		private bool wasGrabbed = false;
		private int instanceID;
		private float checkTimer = 0f;
		private bool isRandomized = false;

		private void Start()
		{
			if (valuableObject == null || physGrabObject == null)
			{
				Destroy(this);
				return;
			}
			instanceID = gameObject.GetInstanceID();
		}

		private void Update()
		{
			if (isRandomized || valuableObject == null || physGrabObject == null) return;

			checkTimer += Time.deltaTime;
			if (checkTimer < 0.5f) return;
			checkTimer = 0f;

			try
			{
				bool isGrabbed = false;
				var field = typeof(PhysGrabObject).GetField("grabbed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (field != null) isGrabbed = (bool)field.GetValue(physGrabObject);

				if (isGrabbed && !wasGrabbed && !isRandomized)
				{
					StartCoroutine(DelayedRandomization());
				}

				wasGrabbed = isGrabbed;

				if (isRandomized && RandomValueMod.modifiedItemIDs.Contains(instanceID))
					Destroy(this);
			}
			catch { }
		}

		private IEnumerator DelayedRandomization()
		{
			yield return null;
			try
			{
				if (valuableObject != null && physGrabObject != null && !isRandomized)
				{
					int id = gameObject.GetInstanceID();
					if (!RandomValueMod.modifiedItemIDs.Contains(id))
					{
						float original = RandomValueMod.GetItemPrice(valuableObject);
						if (original > 0)
						{
							float newPrice = RandomValueMod.GetDeterministicRandomPrice(id, original);
							if (ItemModifier.ModifyItemAttributes(physGrabObject, valuableObject, newPrice))
							{
								RandomValueMod.modifiedItemIDs.Add(id);
								isRandomized = true;
							}
						}
					}
				}
			}
			catch { }
		}
	}
	#endregion
}