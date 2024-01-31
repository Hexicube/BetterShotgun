using System;
using BepInEx.Configuration;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

public class ShotgunConfig {
    private static int numTightPelletsLocal = 3;
    private static float tightPelletAngleLocal = 2.5f;
    private static int numLoosePelletsLocal = 7;
    private static float loosePelletAngleLocal = 10f;

    public static int numTightPellets = 3;
    public static float tightPelletAngle = 2.5f;
    public static int numLoosePellets = 7;
    public static float loosePelletAngle = 10f;

    private static void SetValues(int tightCount, float tightSpread, int looseCount, float looseSpread) {
        numTightPellets = tightCount;
        tightPelletAngle = tightSpread;
        numLoosePellets = looseCount;
        loosePelletAngle = looseSpread;
    }
    private static void SetToLocalValues() => SetValues(numTightPelletsLocal, tightPelletAngleLocal, numLoosePelletsLocal, loosePelletAngleLocal);

    public static void LoadConfig(ConfigFile config) {
        Debug.Log(config);

        numTightPelletsLocal = Math.Clamp(config.Bind("Pellets", "tightPelletCount", 3, "Number of pellets for tight grouping").Value, 0, 100);
        tightPelletAngleLocal = Mathf.Clamp(config.Bind("Pellets", "tightPelletAngle", 2.5f, "Pellet spread for tight grouping (degrees)").Value, 0f, 90f);
        numLoosePelletsLocal = Math.Clamp(config.Bind("Pellets", "loosePelletCount", 7, "Number of pellets for loose grouping").Value, 0, 100);
        loosePelletAngleLocal = Mathf.Clamp(config.Bind("Pellets", "loosePelletAngle", 10f, "Pellet spread for loose grouping (degrees)").Value, 0f, 90f);

        SetToLocalValues();
    }

    public static byte[] GetSettings() {
        byte[] data = new byte[17];
        data[0] = 1;
        Array.Copy(BitConverter.GetBytes(numTightPelletsLocal), 0, data, 1, 4);
        Array.Copy(BitConverter.GetBytes(tightPelletAngleLocal), 0, data, 5, 4);
        Array.Copy(BitConverter.GetBytes(numLoosePelletsLocal), 0, data, 9, 4);
        Array.Copy(BitConverter.GetBytes(loosePelletAngleLocal), 0, data, 13, 4);
        return data;
    }

    public static void SetSettings(byte[] data) {
        switch (data[0]) {
            case 1: {
                numTightPellets = BitConverter.ToInt32(data, 1);
                tightPelletAngle = BitConverter.ToSingle(data, 5);
                numLoosePellets = BitConverter.ToInt32(data, 9);
                loosePelletAngle = BitConverter.ToSingle(data, 13);
                break;
            }
            default: {
                throw new Exception("Invalid version byte");
            }
        }
    }

    // networking

    private static bool IsHost() => NetworkManager.Singleton.IsHost;

    public static void OnRequestSync(ulong clientID, FastBufferReader reader) {
        if (!IsHost()) return;

        Debug.Log("SHOTGUN: Sending config to client " + clientID);
        byte[] data = GetSettings();
        FastBufferWriter dataOut = new(data.Length, Unity.Collections.Allocator.Temp, data.Length);
        try {
            dataOut.WriteBytes(data);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage("HexiShotgun_OnReceiveConfigSync", clientID, dataOut, NetworkDelivery.Reliable);
        }
        catch (Exception e) {
            Debug.LogError("SHOTGUN: Failed to send config: " + e);
        }
        finally {
            dataOut.Dispose();
        }
    }

    public static void OnReceiveSync(ulong clientID, FastBufferReader reader) {
        Debug.Log("SHOTGUN: Received config from host");
        byte[] data = new byte[17];
        try {
            reader.ReadBytes(ref data, 17);
            SetSettings(data);
        }
        catch (Exception e) {
            Debug.LogError("SHOTGUN: Failed to receive config: " + e);
            SetToLocalValues();
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
    static void ServerConnect() {
        if (IsHost()) {
            Debug.Log("SHOTGUN: Started hosting, using local settings");
            SetToLocalValues();
            NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler("HexiShotgun_OnRequestConfigSync", OnRequestSync);
        }
        else {
            Debug.Log("SHOTGUN: Connected to server, requesting settings");
            NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler("HexiShotgun_OnReceiveConfigSync", OnReceiveSync);
            byte[] data = new byte[17];
            FastBufferWriter blankOut = new(data.Length, Unity.Collections.Allocator.Temp);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage("HexiShotgun_OnRequestConfigSync", 0, blankOut, NetworkDelivery.Reliable);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameNetworkManager), "StartDisconnect")]
    static void ServerDisconnect() {
        Debug.Log("SHOTGUN: Server disconnect");
        SetToLocalValues();
    }
}
