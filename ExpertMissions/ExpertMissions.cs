using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;
using System.Reflection;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Linq;

// This is good boilerplate code to paste into all your mods that need to store data in the save file:
public static class SaveFileHelper
{
    // How to use: 
    // Main.myModsSaveContainer = SaveFileHelper.Load<MyModsSaveContainer>("MyModName");
    public static T Load<T>(this string modName) where T : new()
    {
        string xmlStr;
        if (GameState.modData != null && GameState.modData.TryGetValue(modName, out xmlStr)) {
            Debug.Log("Proceeding to parse save data for " + modName);
            System.Xml.Serialization.XmlSerializer xmlSerializer = new System.Xml.Serialization.XmlSerializer(typeof(T));
            using (System.IO.StringReader textReader = new System.IO.StringReader(xmlStr)) {
                return (T)xmlSerializer.Deserialize(textReader);
            }
        }
        Debug.Log("Cannot load data from save file. Using defaults for " + modName);
        return new T();
    }

    // How to use:
    // SaveFileHelper.Save(Main.myModsSaveContainer, "MyModName");
    public static void Save<T>(this T toSerialize, string modName)
    {
        System.Xml.Serialization.XmlSerializer xmlSerializer = new System.Xml.Serialization.XmlSerializer(typeof(T));
        using (System.IO.StringWriter textWriter = new System.IO.StringWriter()) {
            xmlSerializer.Serialize(textWriter, toSerialize);
            GameState.modData[modName] = textWriter.ToString();
            Debug.Log("Packed save data for " + modName);
        }
    }
}

namespace ExpertMissions
{
    public class ExpertMissionsSaveContainer
    {
        public List<int> expertMissionIdxs { get; set; } = new List<int>();
        public int baseSeed { get; set; } = (int)System.DateTime.Now.Ticks;
        public List<int> takenMissions { get; set; } = new List<int>();
    }

    static class Main
    {
        public static bool enabled;
        public static UnityModManager.ModEntry.ModLogger logger;
        // A seed that stays constant for the save game. This makes sure the mission list only changes when we want it to.
        public static int baseSeed;
        // A list where each pair of ints describes a single mission. Each even int is the originPort's index. Each odd int is the totalPrice of the mission.
        public static List<int> takenMissions = new List<int>();

        static bool Load(UnityModManager.ModEntry modEntry)
        {
            logger = modEntry.Logger;
            modEntry.OnToggle = OnToggle;
            baseSeed = (int)System.DateTime.Now.Ticks;

            var harmony = new Harmony(modEntry.Info.Id);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            return true;
        }

        static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            Main.enabled = value;
            if (value) {
                Sun.OnNewDay += NewDayMissions;
            } else {
                Sun.OnNewDay -= NewDayMissions;
            }
            return true;
        }

        static void NewDayMissions()
        {
            if (Main.enabled) {
                Main.takenMissions = new List<int>();
            }
        }

        [HarmonyPatch(typeof(PlayerMissions), "AcceptMission")]
        static class AcceptMissionPatch
        {
            /* Don't alter demand for expert missions */
            private static void Postfix(Mission mission)
            {
                if (Main.enabled && mission.missionName.EndsWith("*")) {
                    mission.destinationPort.IncreaseDemand(mission); //undoes ReduceDemand() from the orig method

                    // remove this expert mission from the Port menu.
                    Main.takenMissions.Add(mission.originPort.portIndex);
                    Main.takenMissions.Add(mission.totalPrice);
                }
            }
        }

        [HarmonyPatch(typeof(PlayerMissions), "AbandonMission")]
        static class AbandonMissionPatch
        {
            /* Don't alter demand for expert missions */
            private static void Prefix(int missionIndex)
            {
                Mission mission = PlayerMissions.missions[missionIndex];
                if (Main.enabled && mission.missionName.EndsWith("*")) {
                    //counteract the IncreaseDemand() call from the orig method
                    mission.destinationPort.island.ChangeDemand(
                        mission.goodPrefab.GetComponent<SaveablePrefab>().prefabIndex, 
                        mission.GetDeliveredCount()-mission.goodCount);
                }
            }
        }

        [HarmonyPatch(typeof(Port), "GenerateMissions")]
        public static class GenerateMissionsPatch
        {
            // A list of missions. Only used to pass data from the Prefix to the Transpiler:
            private static List<Mission> newMissions = new List<Mission>();
            // A method referance that can be used as an operand:
            private static MethodInfo Method__RetrieveExpertMissions = AccessTools.Method(typeof(GenerateMissionsPatch), nameof(GenerateMissionsPatch.RetrieveExpertMissions));

            /* Create the expert missions so we can feed them to the Transpiler */
            private static void Prefix(int page,
                Port __instance,
                ref int ___currentMissionCount, 
                ref GameObject[] ___producedGoodPrefabs,
                ref Port[] ___destinationPorts)
            {
                if (Main.enabled) {
                    newMissions = new List<Mission>();
                    /* x We want to choose a random destination from all of Port.ports
                     * x The number of missions should be based on ___producedGoodPrefabs.Length, except for idxs 6 and 20
                     *   (2 goods -> 1 mission, 20+ goods -> 5 missions)
                     * x goodPrefab can be anything in ___producedGoodPrefabs 
                     * x qty should scale randomly between 8-20
                     * x dueDay should be based on a timeFactor with multiplier 2.0~4.0
                     * x price should be be based on qty, weight, and dueDay
                    */

                    // Bigger ports get more missions, ranging from 1 to 5
                    int nMissions = Mathf.RoundToInt(Mathf.Lerp(1, 5, ___producedGoodPrefabs.Length/20));
                    if (__instance.portIndex == 6 || __instance.portIndex == 20) {
                        // Special exception for Oasis and Happy Bay
                        nMissions = 5;
                    }

                    // Set a deterministic seed for this unique port and day
                    int newSeed = Main.baseSeed + __instance.portIndex + 100 * GameState.day;
                    logger.Log("today's seed:" + newSeed);
                    var rand = new System.Random(newSeed);
                    // Generate all expert missions for this port
                    for (int i=0; i<nMissions; i++) {
                        // Any port can be a destination, except this port and TestPort (index=7)
                        Port destination = null;
                        while (destination == null || destination.portIndex == __instance.portIndex || destination.portIndex==7) {
                            destination = Port.ports[rand.Next(0, Port.ports.Length)];
                        }
                        // Choose any good this port exports
                        GameObject goodPrefab = ___producedGoodPrefabs[rand.Next(0, ___producedGoodPrefabs.Length)];
                        // Choose a goods quantity in the range 8-20
                        int qty = rand.Next(8, 21);
                        // Set due date using a random speedFactor similar to the value used in vanilla missions for gold cargo
                        float distMeters = Vector3.Distance(__instance.gameObject.transform.position, destination.transform.position);
                        float randomFactor = (float)rand.NextDouble() * 2f + 2f;
                        float speedFactor = 2f * (Sun.sun.GetRealtimeDayLength() / 60f / 60f) * randomFactor;
                        int dueDay = GameState.day + Mathf.RoundToInt(distMeters / 1000f / speedFactor) + 1;
                        // Price depends on weight, qty, and speedFactor
                        int totalPrice = GetTotalPrice(distMeters/100f, qty, GetCargoWeight(goodPrefab), randomFactor);

                        Mission myMission = new Mission(__instance, destination, goodPrefab, qty, totalPrice, 1f, 0, dueDay);
                        myMission.missionName += "*";
                        newMissions.Add(myMission);
                    }
                    // Omit missions that have already been taken
                    for (int i=0; i<Main.takenMissions.Count-1; i+=2) {
                        if (takenMissions[i] == __instance.portIndex) {
                            int takenPrice = takenMissions[i+1];
                            for (int j=0; j<newMissions.Count; j++) {
                                // We assume totalPrice is unique. Tiny chance for glitches here.
                                if (newMissions[j] != null && newMissions[j].totalPrice == takenPrice) {
                                    newMissions.RemoveAt(j);
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            /* This Transpiler concatenates the list created by the Prefix onto the mission list
            * it effectively adds one line of c#: 
            *     list.AddRange(RetrieveExpertMissions());
            * that line needs to be inserted just before this line from the vanilla method:
            *     list.Sort((Mission s2, Mission s1) => s1.pricePerKm.CompareTo(s2.pricePerKm));
            */
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                // Look at the entire method, searching for the line where we sort the mission list
                int insertionIndex = -1;
                List<CodeInstruction> il = instructions.ToList();
                for (int i = 1; i < il.Count; ++i) {
                    if (il[i].opcode == OpCodes.Ldsfld &&
                        string.Concat(il[i].operand).StartsWith("System.Comparison") &&
                        il[i-1].IsLdloc()) {
                        insertionIndex = i-1;
                        break;
                    }
                }
                // Add the 3 IL operations that perform list.AddRange(RetrieveExpertMissions());
                if (insertionIndex != -1) {
                    var instructionsToInsert = new List<CodeInstruction>();
                    //ldloc.1
                    instructionsToInsert.Add(il[insertionIndex]);
                    // call      class [mscorlib] System.Collections.Generic.List`1<class Mission> RetrieveExpertMissions()
                    instructionsToInsert.Add(new CodeInstruction(OpCodes.Call, Method__RetrieveExpertMissions));
                    // callvirt  instance void class [mscorlib] System.Collections.Generic.List`1<class Mission>::AddRange(class [mscorlib] System.Collections.Generic.IEnumerable`1<!0>)
                    instructionsToInsert.Add(new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(List<Mission>), nameof(List<Mission>.AddRange))) );
                    il.InsertRange(insertionIndex, instructionsToInsert);
                    logger.Log("Successfully transpiled Port.GenerateMissions()");
                }
                // Return the entire method code with modifications
                return il.AsEnumerable();
            }

            private static List<Mission> RetrieveExpertMissions()
            {
                return newMissions;
            }
        }

        [HarmonyPatch(typeof(Mission), "GetDeliveryRep")]
        public static class MissionRepPatch
        {
            /* Scale down rep if this is an expert mission */
            private static void Postfix(Mission __instance, ref int __result)
            {
                if (Main.enabled && __instance.missionName.EndsWith("*")) {
                    // Override the return value with a rep based on the totalPrice
                    float repEach = __instance.totalPrice * 0.7f / __instance.goodCount;
                    __result = Mathf.RoundToInt(repEach);
                }
            }
        }
        
        [HarmonyPatch(typeof(Mission), "GetDeliveryPrice")]
        public static class MissionGoldPatch
        {
            /* Penalize late deliveries harshly if this is an expert mission */
            private static void Postfix(Mission __instance, ref int __result)
            {
                if (Main.enabled && __instance.missionName.EndsWith("*")) {
                    // Override the return value with a new price
                    float pricePer = (float)__instance.totalPrice / (float)__instance.goodCount;
                    int daysLate = GameState.day - __instance.dueDay;
                    if (daysLate < 0) {
                        daysLate = 0;
                    }
                    float reward = pricePer / (1 + daysLate);
                    __result = Mathf.RoundToInt(reward);
                }
            }
        }

        [HarmonyPatch(typeof(SaveLoadManager), "LoadModData")]
        static class LoadGamePatch
        {
            private static void Postfix()
            {
                if (Main.enabled) {
                    //Load entire ExpertMissionsSaveContainer from save file
                    var saved = SaveFileHelper.Load<ExpertMissionsSaveContainer>(typeof(Main).Namespace);
                    // Mark expert missions with '*'
                    foreach (int i in saved.expertMissionIdxs) {
                        PlayerMissions.missions[i].missionName += "*";
                    }
                    // Get seed for generating missions
                    Main.baseSeed = saved.baseSeed;
                    // Get the list of missions that have been taken on this same day
                    Main.takenMissions = saved.takenMissions;
                }
            }
        }

        [HarmonyPatch(typeof(SaveLoadManager), "SaveModData")]
        static class SaveGamePatch
        {
            private static void Postfix()
            {
                if (Main.enabled) {
                    var toSave = new ExpertMissionsSaveContainer();
                    for (int i = 0; i<PlayerMissions.missions.Length; i++) {
                        if (PlayerMissions.missions[i] != null && PlayerMissions.missions[i].missionName.EndsWith("*")) {
                            toSave.expertMissionIdxs.Add(i);
                        }
                    }
                    toSave.baseSeed = Main.baseSeed;
                    toSave.takenMissions = Main.takenMissions;
                    SaveFileHelper.Save(toSave, typeof(Main).Namespace);
                }
            }
        }

        private static float GetCargoWeight(GameObject goodPrefab)
        {
            ShipItemCrate component = goodPrefab.GetComponent<ShipItemCrate>();
            ShipItemBottle component2 = goodPrefab.GetComponent<ShipItemBottle>();
            if (component) {
                return component.mass + component.GetContainedPrefab().GetComponent<ShipItem>().mass * component.amount;
            }
            if (component2) {
                return component2.mass + component2.health;
            }
            return goodPrefab.GetComponent<ShipItem>().mass;
        }

        private static int GetTotalPrice(float distMiles, int qty, float boxWeight, float speedFactor)
        {
            // These have been tuned to be only moderately higher than vanilla mission rewards, except in the really extreme missions
            float basePrice = 3f * distMiles;
            float distBonus = 1f + distMiles * 0.00063f;
            float weightBonus = 1f + boxWeight * qty / 1000f;
            float qtyBonus = 1f + qty * 0.2f;
            float speedBonus = speedFactor / 2.5f;
            return Mathf.RoundToInt(basePrice * distBonus * weightBonus * qtyBonus * speedBonus * 0.6f);
        }
    }
}
