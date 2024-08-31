﻿using Jotunn.Managers;
using PlanBuild.Plans;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

using Logger = Jotunn.Logger;

namespace PlanBuild.Blueprints
{
    internal static class BlueprintManager
    {
        public static BlueprintDictionary LocalBlueprints;
        public static BlueprintDictionary TemporaryBlueprints;
        public static BlueprintDictionary ServerBlueprints;

        public const float HighlightTimeout = 0.5f;
        public const float GhostTimeout = 10f;

        public static Piece LastHoveredPiece;

        private static float LastHighlightTime;
        private static float OriginalPlaceDistance;
        private static GameObject OriginalTooltip;

        public static void Init()
        {
            Logger.LogInfo("Initializing BlueprintManager");

            try
            {
                // Init stuff
                LocalBlueprints = new BlueprintDictionary();
                TemporaryBlueprints = new BlueprintDictionary();
                ServerBlueprints = new BlueprintDictionary();
                Selection.Init();
                SelectionCommands.Init();
                BlueprintSync.Init();
                BlueprintCommands.Init();
                UndoManager.Instance.CreateQueue(Config.BlueprintUndoQueueNameConfig.Value);

                // Hooks
                On.ZNetScene.Shutdown += (orig, self) =>
                {
                    orig(self);
                    TemporaryBlueprints.Clear();
                    Selection.Instance.Clear();
                };
                On.Player.OnSpawned += Player_OnSpawned;
                On.PieceTable.UpdateAvailable += PieceTable_UpdateAvailable;
                On.Player.SetupPlacementGhost += Player_SetupPlacementGhost;
                On.Player.UpdatePlacementGhost += Player_UpdatePlacementGhost;
                On.Player.PieceRayTest += Player_PieceRayTest;
                On.Humanoid.EquipItem += Humanoid_EquipItem;
                On.Humanoid.UnequipItem += Humanoid_UnequipItem;
                On.Hud.TogglePieceSelection += Hud_TogglePieceSelection;
                On.Piece.Awake += Piece_Awake;
                On.Piece.OnDestroy += Piece_OnDestroy;

                GUIManager.OnCustomGUIAvailable += GUIManager_OnCustomGUIAvailable;
                On.UITooltip.OnHoverStart += UITooltip_OnHoverStart;

                // Ghost watchdog
                IEnumerator watchdog()
                {
                    while (true)
                    {
                        foreach (var bp in LocalBlueprints.Values.Where(x => x.GhostActiveTime > 0f))
                        {
                            if (Time.time - bp.GhostActiveTime > GhostTimeout)
                            {
                                bp.DestroyGhost();
                            }
                        }

                        yield return new WaitForSeconds(GhostTimeout);
                    }
                }

                PlanBuildPlugin.Instance.StartCoroutine(watchdog());
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Error caught while initializing: {ex}");
            }
        }

        /// <summary>
        ///     Determine if a piece can be captured in a blueprint
        /// </summary>
        /// <param name="piece">Piece instance to be tested</param>
        /// <param name="onlyPlanned">When true, only pieces with the PlanPiece component return true</param>
        /// <returns></returns>
        public static bool CanCapture(Piece piece, bool onlyPlanned = false)
        {
            if (piece.name.StartsWith(BlueprintAssets.PieceSnapPointName) || piece.name.StartsWith(BlueprintAssets.PieceCenterPointName))
            {
                return true;
            }

            if (piece.name.StartsWith(Blueprint.PieceBlueprintName))
            {
                return false;
            }

            if (!SynchronizationManager.Instance.PlayerIsAdmin && PlanBlacklist.Contains(piece))
            {
                return false;
            }

            return piece.GetComponent<PlanPiece>() != null || (!onlyPlanned && PlanDB.Instance.CanCreatePlan(piece));
        }

        /// <summary>
        ///     Get all pieces on a given position in a given radius, optionally only planned ones
        /// </summary>
        /// <param name="position"></param>
        /// <param name="radius"></param>
        /// <param name="onlyPlanned"></param>
        /// <returns></returns>
        public static List<Piece> GetPiecesInRadius(Vector3 position, float radius, bool onlyPlanned = false)
        {
            List<Piece> result = new List<Piece>();
            foreach (var piece in Piece.s_allPieces)
            {
                Vector3 piecePos = piece.transform.position;
                if (Vector2.Distance(new Vector2(position.x, position.z), new Vector2(piecePos.x, piecePos.z)) <= radius
                    && CanCapture(piece, onlyPlanned))
                {
                    result.Add(piece);
                }
            }
            return result;
        }

        /// <summary>
        ///     "Highlights" pieces in a given radius with a given color.
        /// </summary>
        public static void HighlightPiecesInRadius(Vector3 startPosition, float radius, Color color, bool onlyPlanned = false)
        {
            if (Time.time < LastHighlightTime + HighlightTimeout)
            {
                return;
            }

            foreach (var piece in GetPiecesInRadius(startPosition, radius, onlyPlanned))
            {
                if (piece.TryGetComponent(out WearNTear wearNTear))
                {
                    wearNTear.Highlight(color, HighlightTimeout + 0.1f);
                }
            }
            LastHighlightTime = Time.time;
        }

        /// <summary>
        ///     "Highlights" the last hovered piece with a given color.
        /// </summary>
        public static void HighlightHoveredPiece(Color color, bool onlyPlanned = false)
        {
            if (Time.time < LastHighlightTime + HighlightTimeout)
            {
                return;
            }

            if (LastHoveredPiece)
            {
                if (onlyPlanned && !LastHoveredPiece.GetComponent<PlanPiece>())
                {
                    return;
                }
                if (LastHoveredPiece.TryGetComponent(out WearNTear wearNTear))
                {
                    wearNTear.Highlight(color, HighlightTimeout + 0.1f);
                }
            }
            LastHighlightTime = Time.time;
        }

        /// <summary>
        ///     Get the GameObject from a ZDOID via ZNetScene or force creation of one via ZDO
        /// </summary>
        public static GameObject GetGameObject(ZDOID zdoid, bool required = false)
        {
            GameObject go = ZNetScene.instance.FindInstance(zdoid);
            if (go)
            {
                return go;
            }
            return required ? ZNetScene.instance.CreateObject(ZDOMan.instance.GetZDO(zdoid)) : null;
        }

        public static bool ClearClipboard()
        {
            if (TemporaryBlueprints.Count == 0)
            {
                return false;
            }

            foreach (var tmp in TemporaryBlueprints)
            {
                tmp.Value.DestroyBlueprint();
            }
            TemporaryBlueprints.Clear();
            Player.m_localPlayer.UpdateKnownRecipesList();
            Player.m_localPlayer.UpdateAvailablePiecesList();
            BlueprintGUI.RefreshBlueprints(BlueprintLocation.Temporary);

            return true;
        }

        /// <summary>
        ///     Create pieces for all known local Blueprints
        /// </summary>
        public static void RegisterKnownBlueprints()
        {
            if (Player.m_localPlayer)
            {
                Logger.LogInfo("Registering known blueprints");

                foreach (var bp in LocalBlueprints.Values)
                {
                    bp.CreatePiece();
                }
                Player.m_localPlayer.UpdateKnownRecipesList();
                Player.m_localPlayer.UpdateAvailablePiecesList();
            }
        }

        /// <summary>
        ///     Create blueprint pieces on player spawn
        /// </summary>
        private static void Player_OnSpawned(On.Player.orig_OnSpawned orig, Player self, bool spawnValkrie)
        {
            orig(self, spawnValkrie);
            if (self == Player.m_localPlayer)
            {
                RegisterKnownBlueprints();

                if (!Config.AllowBlueprintRune.Value && !SynchronizationManager.Instance.PlayerIsAdmin)
                {
                    Player.m_localPlayer.SetBuildCategory(0);
                    Player.m_localPlayer.SetSelectedPiece(new Vector2Int(9, 9));
                }
            }
        }

        /// <summary>
        ///     Reorder pieces in local blueprint categories by name.
        ///     Remove "placeholder pieces" from blueprint categories
        /// </summary>
        private static void PieceTable_UpdateAvailable(On.PieceTable.orig_UpdateAvailable orig, PieceTable self, HashSet<string> knownRecipies, Player player, bool hideUnavailable, bool noPlacementCost)
        {
            orig(self, knownRecipies, player, hideUnavailable, noPlacementCost);

            if (self.name.Equals(BlueprintAssets.PieceTableName))
            {
                foreach (var cats in LocalBlueprints.Values.GroupBy(x => x.Category))
                {
                    Piece.PieceCategory? cat = PieceManager.Instance.GetPieceCategory(cats.Key);
                    if (cat.HasValue)
                    {
                        List<Piece> reorder = new List<Piece>();
                        reorder.Add(BlueprintAssets.PlaceholderObject.GetComponent<Piece>());
                        reorder.AddRange(self.m_availablePieces[(int)cat]
                            .OrderBy(x => x.m_name)
                            .Where(x => !x.name.Equals(BlueprintAssets.PiecePlaceholderName))
                            .ToList());
                        self.m_availablePieces[(int)cat] = reorder;
                    }
                }
            }
        }

        /// <summary>
        ///     Lazy ghost instantiation
        /// </summary>
        private static void Player_SetupPlacementGhost(On.Player.orig_SetupPlacementGhost orig, Player self)
        {
            if (self.m_buildPieces == null)
            {
                orig(self);
                return;
            }

            GameObject prefab = self.m_buildPieces.GetSelectedPrefab();
            if (!prefab || !prefab.name.StartsWith(Blueprint.PieceBlueprintName))
            {
                orig(self);
                return;
            }

            string bpname = prefab.name.Substring(Blueprint.PieceBlueprintName.Length + 1);
            if (LocalBlueprints.TryGetValue(bpname, out var bp))
            {
                bp.InstantiateGhost();
            }

            orig(self);
        }

        /// <summary>
        ///     Timed ghost destruction
        /// </summary>
        private static void Player_UpdatePlacementGhost(On.Player.orig_UpdatePlacementGhost orig, Player self, bool flashGuardStone)
        {
            if (self.m_buildPieces == null)
            {
                orig(self, flashGuardStone);
                return;
            }

            GameObject prefab = self.m_buildPieces.GetSelectedPrefab();
            if (!prefab || !prefab.name.StartsWith(Blueprint.PieceBlueprintName))
            {
                orig(self, flashGuardStone);
                return;
            }

            string bpname = prefab.name.Substring(Blueprint.PieceBlueprintName.Length + 1);
            if (LocalBlueprints.TryGetValue(bpname, out var bp))
            {
                bp.GhostActiveTime = Time.time;
            }

            orig(self, flashGuardStone);
        }

        /// <summary>
        ///     Save the reference to the last hovered piece
        /// </summary>
        private static bool Player_PieceRayTest(On.Player.orig_PieceRayTest orig, Player self, out Vector3 point, out Vector3 normal, out Piece piece, out Heightmap heightmap, out Collider waterSurface, bool water)
        {
            bool result = orig(self, out point, out normal, out piece, out heightmap, out waterSurface, water);
            LastHoveredPiece = piece;
            return result;
        }

        /// <summary>
        ///     BlueprintRune equip
        /// </summary>
        private static bool Humanoid_EquipItem(On.Humanoid.orig_EquipItem orig, Humanoid self, ItemDrop.ItemData item, bool triggerEquipEffects)
        {
            bool result = orig(self, item, triggerEquipEffects);
            if (result && Player.m_localPlayer?.m_rightItem?.m_shared.m_name == BlueprintAssets.BlueprintRuneItemName)
            {
                OriginalPlaceDistance = Math.Max(Player.m_localPlayer.m_maxPlaceDistance, 8f);
                Player.m_localPlayer.m_maxPlaceDistance = Config.RayDistanceConfig.Value;

                var desc = Hud.instance.m_buildHud.transform.Find("SelectedInfo/selected_piece/piece_description");
                if (desc is RectTransform rect)
                {
                    rect.pivot = new Vector2(0.5f, 1f);
                    rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, -30f);
                    rect.sizeDelta = new Vector2(rect.sizeDelta.x, 110f);
                }

                if (!Config.AllowBlueprintRune.Value && !SynchronizationManager.Instance.PlayerIsAdmin)
                {
                    if (Hud.IsPieceSelectionVisible())
                    {
                        Hud.HidePieceSelection();
                    }
                    Player.m_localPlayer.SetBuildCategory(0);
                    Player.m_localPlayer.SetSelectedPiece(new Vector2Int(9, 9));
                }
            }
            return result;
        }

        /// <summary>
        ///     BlueprintRune uneqip
        /// </summary>
        private static void Humanoid_UnequipItem(On.Humanoid.orig_UnequipItem orig, Humanoid self, ItemDrop.ItemData item, bool triggerEquipEffects)
        {
            orig(self, item, triggerEquipEffects);
            if (Player.m_localPlayer &&
                item != null && item.m_shared.m_name == BlueprintAssets.BlueprintRuneItemName)
            {
                Player.m_localPlayer.m_maxPlaceDistance = OriginalPlaceDistance;

                var desc = Hud.instance.m_buildHud.transform.Find("SelectedInfo/selected_piece/piece_description");
                if (desc is RectTransform rect)
                {
                    rect.sizeDelta = new Vector2(rect.sizeDelta.x, 36.5f);
                }
            }
        }

        /// <summary>
        ///     Prevent opening the build menu when the rune is selected and globally disabled
        /// </summary>
        private static void Hud_TogglePieceSelection(On.Hud.orig_TogglePieceSelection orig, Hud self)
        {
            if (Player.m_localPlayer.m_rightItem?.m_shared.m_name == BlueprintAssets.BlueprintRuneItemName &&
                !Hud.IsPieceSelectionVisible() &&
                !Config.AllowBlueprintRune.Value &&
                !SynchronizationManager.Instance.PlayerIsAdmin)
            {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, "$msg_blueprintrune_disabled");
                Player.m_localPlayer.SetBuildCategory(0);
                Player.m_localPlayer.SetSelectedPiece(new Vector2Int(9, 9));
                return;
            }
            orig(self);
        }

        private static void Piece_Awake(On.Piece.orig_Awake orig, Piece self)
        {
            orig(self);
            Selection.Instance.OnPieceAwake(self);
        }

        private static void Piece_OnDestroy(On.Piece.orig_OnDestroy orig, Piece self)
        {
            orig(self);
            Selection.Instance.OnPieceUnload(self);
        }

        // Get all prefabs for this GUI session
        private static void GUIManager_OnCustomGUIAvailable()
        {
            OriginalTooltip = PrefabManager.Instance.GetPrefab("Tooltip");
        }

        /// <summary>
        ///     Display the blueprint tooltip panel when a blueprint building item is hovered
        /// </summary>
        private static void UITooltip_OnHoverStart(On.UITooltip.orig_OnHoverStart orig, UITooltip self, GameObject go)
        {
            if (BlueprintAssets.BlueprintTooltip && Hud.IsPieceSelectionVisible())
            {
                var piece = Hud.instance.m_hoveredPiece;
                if (ZInput.IsGamepadActive() && !ZInput.IsMouseActive())
                {
                    piece = Player.m_localPlayer.GetSelectedPiece();
                }
                if (Config.TooltipEnabledConfig.Value && piece &&
                    piece.name.StartsWith(Blueprint.PieceBlueprintName) &&
                    LocalBlueprints.TryGetValue(piece.name.Substring(Blueprint.PieceBlueprintName.Length + 1), out var bp) &&
                    bp.Thumbnail != null)
                {
                    self.m_tooltipPrefab = BlueprintAssets.BlueprintTooltip;
                    orig(self, go);
                    UITooltip.m_tooltip.transform.Find("Background")
                        .GetComponent<Image>().color = Config.TooltipBackgroundConfig.Value;
                    UITooltip.m_tooltip.transform.Find("Background/BPImage")
                        .GetComponent<Image>().sprite = Sprite.Create(bp.Thumbnail, new Rect(0, 0, bp.Thumbnail.width, bp.Thumbnail.height), Vector2.zero);
                    UITooltip.m_tooltip.transform.Find("Background/BPText")
                        .GetComponent<Text>().text = bp.Name;
                }
                else
                {
                    self.m_tooltipPrefab = OriginalTooltip;
                    orig(self, go);
                }

                return;
            }

            orig(self, go);
        }
    }
}