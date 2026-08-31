using HarmonyLib;
using MelonLoader;
using Il2CppAccount;
using Il2CppAssets.Scripts.GameCore.Managers;

namespace AccuracyIndicator;

// 方案 A：拦截结算时的分数上传，防止使用 Mod 时把成绩上传到服务器。
// 通过 Harmony Prefix 返回 false，直接跳过 UploadScore 原方法。
// 两个入口都要拦：StatisticsManager（统计上传）和 GameAccountSystem（账号系统上传，带谱面数据与回调）。
internal static class ScoreUploadBlocker
{
    // 统计管理器：UploadScore(SceneUploadResultData)
    [HarmonyPatch(typeof(StatisticsManager), "UploadScore")]
    internal static class BlockStatisticsUploadScore
    {
        private static bool Prefix()
        {
            MelonLogger.Msg("[ManiaInMuse] Blocked score upload: StatisticsManager.UploadScore");
            return false;
        }
    }

    // 账号系统：UploadScore(SceneUploadResultData, List<Beat>, Action<int>)
    [HarmonyPatch(typeof(GameAccountSystem), "UploadScore")]
    internal static class BlockGameAccountUploadScore
    {
        private static bool Prefix()
        {
            MelonLogger.Msg("[ManiaInMuse] Blocked score upload: GameAccountSystem.UploadScore");
            return false;
        }
    }
}
