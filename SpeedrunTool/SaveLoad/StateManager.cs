using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Celeste.Mod.SpeedrunTool.Extensions;
using Celeste.Mod.SpeedrunTool.RoomTimer;
using Force.DeepCloner;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Monocle;
using MonoMod.Utils;
using static Celeste.Mod.SpeedrunTool.ButtonConfigUi;

namespace Celeste.Mod.SpeedrunTool.SaveLoad {
    public sealed class StateManager {
        private static SpeedrunToolSettings Settings => SpeedrunToolModule.Settings;

        private Level savedLevel;
        private List<Entity> savedEntities;
        private CassetteBlockManager savedCassetteBlockManager;
        private bool IsSaved => savedLevel != null;

        private float savedFreezeTimer;
        private float savedTimeRate;
        private float savedGlitchValue;
        private float savedDistortAnxiety;
        private float savedDistortGameRate;

        private Dictionary<EverestModule, EverestModuleSession> savedModSessions;

        private States state = States.None;

        private enum States {
            None,
            Loading,
        }

        private readonly List<int> disabledSaveStates = new List<int> {
            Player.StReflectionFall,
            Player.StTempleFall,
            Player.StCassetteFly,
            Player.StIntroJump,
            Player.StIntroWalk,
            Player.StIntroRespawn,
            Player.StIntroWakeUp,
            Player.StDummy,
        };

        public void OnInit() {
            // Clone 开始时，判断哪些类型是直接使用原对象而不 DeepClone 的
            DeepCloner.AddKnownTypesProcessor((type) => {
                if (
                    // Celeste Singleton
                    type == typeof(Celeste)
                    || type == typeof(Settings)

                    // Everest
                    || type == typeof(ModAsset)

                    // Monocle
                    || type == typeof(GraphicsDevice)
                    || type == typeof(GraphicsDeviceManager)
                    || type == typeof(Monocle.Commands)
                    || type == typeof(Pooler)
                    || type == typeof(BitTag)
                    || type == typeof(Atlas)
                    || type == typeof(VirtualTexture)

                    // XNA GraphicsResource
                    || type.IsSubclassOf(typeof(GraphicsResource))
                ) {
                    return true;
                }

                return null;
            });

            // Clone 对象的字段前，判断哪些类型是直接使用原对象而不 DeepClone 的
            DeepCloner.AddPreCloneProcessor(sourceObj => {
                if (sourceObj is Level) {
                    // 金草莓死亡或者 PageDown/Up 切换房间后等等改变 Level 实例的情况
                    if (Engine.Scene is Level level) return level;
                    return sourceObj;
                }

                if (sourceObj is Entity entity && entity.TagCheck(Tags.Global)
                                               && !(entity is Textbox)
                                               && !(entity is SeekerBarrierRenderer)
                                               && !(entity is LightningRenderer)
                ) return sourceObj;

                return null;
            });

            // Clone 对象的字段后，进行自定的处理
            DeepCloner.AddPostCloneProcessor((sourceObj, clonedObj) => {
                if (clonedObj == null) return null;

                // 修复：DeepClone 的 hashSet.Containes(里面存在的引用对象) 总是返回 False，Dictionary 无此问题
                if (clonedObj.GetType().IsHashSet(out Type type) && !type.IsSimple()) {
                    IEnumerator enumerator = ((IEnumerable) clonedObj).GetEnumerator();

                    List<object> backup = new List<object>();
                    while (enumerator.MoveNext()) {
                        backup.Add(enumerator.Current);
                    }

                    clonedObj.InvokeMethod("Clear");

                    backup.ForEach(obj => { clonedObj.InvokeMethod("Add", obj); });
                }

                return clonedObj;
            });
        }

        #region Hook

        public void OnLoad() {
            On.Celeste.Level.Update += CheckButtonsOnLevelUpdate;
            On.Monocle.Scene.Begin += ClearStateWhenSwitchScene;
            On.Celeste.PlayerDeadBody.End += AutoLoadStateWhenDeath;
        }

        public void OnUnload() {
            On.Celeste.Level.Update -= CheckButtonsOnLevelUpdate;
            On.Monocle.Scene.Begin -= ClearStateWhenSwitchScene;
            On.Celeste.PlayerDeadBody.End -= AutoLoadStateWhenDeath;
        }

        private void CheckButtonsOnLevelUpdate(On.Celeste.Level.orig_Update orig, Level self) {
            orig(self);
            CheckButton(self, self.GetPlayer());
        }

        private void ClearStateWhenSwitchScene(On.Monocle.Scene.orig_Begin orig, Scene self) {
            if (self is Overworld) ClearState();
            if (IsSaved) {
                if (self is Level) state = States.None; // 修复：读档途中按下 PageDown/Up 后无法存档
                if (self.GetSession() is Session session && session.Area != savedLevel.Session.Area) {
                    ClearState();
                }
            }
        }

        private void AutoLoadStateWhenDeath(On.Celeste.PlayerDeadBody.orig_End orig, PlayerDeadBody self) {
            if (SpeedrunToolModule.Settings.Enabled
                && SpeedrunToolModule.Settings.AutoLoadAfterDeath
                && IsSaved
                && !(bool) self.GetFieldValue("finished")
            ) {
                if (self.Scene is Level level) {
                    level.OnEndOfFrame += () => LoadState(level);
                    self.RemoveSelf();
                }
            } else {
                orig(self);
            }
        }

        #endregion

        private void SaveState(Level level) {
            ClearState(false);

            savedLevel = level.ShallowClone();
            savedLevel.Session = level.Session.DeepClone();
            savedLevel.Camera = level.Camera.DeepClone();

            savedEntities = DeepCloneEntities(GetEntitiesExcludingGlobal(level));

            savedCassetteBlockManager = level.Entities.FindFirst<CassetteBlockManager>()?.ShallowClone();

            savedFreezeTimer = Engine.FreezeTimer;
            savedTimeRate = Engine.TimeRate;
            savedGlitchValue = Glitch.Value;
            savedDistortAnxiety = Distort.Anxiety;
            savedDistortGameRate = Distort.GameRate;

            // save all mod sessions
            savedModSessions = new Dictionary<EverestModule, EverestModuleSession>();
            foreach (EverestModule module in Everest.Modules) {
                if (module._Session != null) {
                    savedModSessions[module] = module._Session.DeepCloneYaml(module.SessionType);
                }
            }

            LoadState(level);
        }

        private void LoadState(Level level) {
            if (!IsSaved) return;

            state = States.Loading;

            RoomTimerManager.Instance.ResetTime();

            level.SetFieldValue("transition", null); // 允许切换房间时读档
            level.Displacement.Clear(); // 避免冲刺后读档残留爆破效果
            TrailManager.Clear(); // 清除冲刺的残影

            UnloadLevelEntities(level);
            RestoreLevelEntities(level);
            RestoreCassetteBlockManager(level);
            RestoreLevel(level);

            // restore all mod sessions
            foreach (EverestModule module in Everest.Modules) {
                if (savedModSessions.TryGetValue(module, out EverestModuleSession savedModSession)) {
                    module._Session = savedModSession.DeepCloneYaml(module.SessionType);
                }
            }

            level.Frozen = true; // 加一个转场等待，避免太突兀
            level.TimerStopped = true; // 停止计时器

            // 修复问题：未打开自动读档时，死掉按下确认键后读档完成会接着执行 Reload 复活方法
            if (level.RendererList.Renderers.FirstOrDefault(renderer => renderer is ScreenWipe) is ScreenWipe wipe) {
                wipe.Cancel();
            }

            level.DoScreenWipe(true, () => {
                level.Frozen = false;
                state = States.None;
                RestoreLevel(level);
                RoomTimerManager.Instance.SavedEndPoint?.ReadyForTime();
            });
        }

        private List<Entity> DeepCloneEntities(List<Entity> entities) {
            Dictionary<int, Dictionary<string, object>> dynDataDict = new Dictionary<int, Dictionary<string, object>>();
            EntitiesWrapper entitiesWrapper = new EntitiesWrapper(entities, dynDataDict);

            // Find the dynData.Data that need to be cloned
            for (int i = 0; i < entities.Count; i++) {
                Entity entity = entities[i];
                if (DynDataUtils.GetDataMap(entity.GetType())?.Count == 0) continue;

                if (DynDataUtils.GetDate(entity) is Dictionary<string, object> data && data.Count > 0) {
                    dynDataDict.Add(i, data);
                }
            }

            // DeepClone together make them share same object.
            EntitiesWrapper clonedEntitiesWrapper = entitiesWrapper.DeepClone();

            // Copy dynData.Data
            Dictionary<int, Dictionary<string, object>> clonedDynDataDict = entitiesWrapper.DynDataDict;
            foreach (int i in clonedDynDataDict.Keys) {
                Entity clonedEntity = clonedEntitiesWrapper.Entities[i];
                if (DynDataUtils.GetDate(clonedEntity) is Dictionary<string, object> data) {
                    Dictionary<string, object> clonedData = clonedEntitiesWrapper.DynDataDict[i];
                    foreach (string key in clonedData.Keys) {
                        data[key] = clonedData[key];
                    }
                }
            }

            return clonedEntitiesWrapper.Entities;
        }

        private void UnloadLevelEntities(Level level) {
            List<Entity> entities = GetEntitiesExcludingGlobal(level);
            level.Remove(entities);
            level.Entities.UpdateLists();
            // 修复：Retry 后读档依然执行 PlayerDeadBody.End 的问题
            // 由 level.CopyAllSimpleTypeFields(savedLevel) 自动处理了
            // level.RetryPlayerCorpse = null;
        }

        private void RestoreLevelEntities(Level level) {
            List<Entity> deepCloneEntities = DeepCloneEntities(savedEntities);

            // Re Add Entities
            List<Entity> entities = (List<Entity>) level.Entities.GetFieldValue("entities");
            HashSet<Entity> current = (HashSet<Entity>) level.Entities.GetFieldValue("current");
            foreach (Entity entity in deepCloneEntities) {
                if (entities.Contains(entity)) continue;
                if (entity is ConfettiRenderer) continue; // 不恢复自定义终点的触碰效果

                current.Add(entity);
                entities.Add(entity);

                level.TagLists.InvokeMethod("EntityAdded", entity);
                level.Tracker.InvokeMethod("EntityAdded", entity);
                entity.Components?.ToList()
                    .ForEach(component => {
                        // LightingRenderer 需要，不然不会发光
                        if (component is VertexLight vertexLight) vertexLight.Index = -1;
                        level.Tracker.InvokeMethod("ComponentAdded", component);
                    });
                level.InvokeMethod("SetActualDepth", entity);
                Dictionary<Type, Queue<Entity>> pools =
                    (Dictionary<Type, Queue<Entity>>) Engine.Pooler.GetPropertyValue("Pools");
                Type type = entity.GetType();
                if (pools.ContainsKey(type) && pools[type].Count > 0) {
                    pools[type].Dequeue();
                }
            }
        }

        private void RestoreCassetteBlockManager(Level level) {
            if (savedCassetteBlockManager != null) {
                level.Entities.FindFirst<CassetteBlockManager>()?.CopyAllSimpleTypeFields(savedCassetteBlockManager);
            }
        }

        private void RestoreLevel(Level level) {
            savedLevel.Session.DeepCloneTo(level.Session);
            level.Camera.CopyFrom(savedLevel.Camera);
            level.CopyAllSimpleTypeFields(savedLevel);

            // External Instance Value
            Engine.FreezeTimer = savedFreezeTimer;
            Engine.TimeRate = savedTimeRate;
            Glitch.Value = savedGlitchValue;
            Distort.Anxiety = savedDistortAnxiety;
            Distort.GameRate = savedDistortGameRate;
        }

        private List<Entity> GetEntitiesExcludingGlobal(Level level) {
            var result = new List<Entity>();
            foreach (Entity entity in level.Entities) {
                if (!entity.TagCheck(Tags.Global) || entity is Textbox) {
                    result.Add(entity);
                }
            }

            if (level.GetPlayer() is Player player) {
                // Player 被 Remove 时会触发其他 Trigger，所以必须最早清除
                result.Remove(player);
                result.Insert(0, player);
            }

            // 存储的 Entity 被清除时会调用 Renderer，所以 Renderer 应该放到最后
            if (level.Entities.FindFirst<SeekerBarrierRenderer>() is Entity seekerBarrierRenderer) {
                result.Add(seekerBarrierRenderer);
            }

            if (level.Entities.FindFirst<LightningRenderer>() is Entity lightningRenderer) {
                result.Add(lightningRenderer);
            }

            return result;
        }

        private void ClearState(bool clearEndPoint = true) {
            if (Engine.Scene is Level level && IsNotCollectingHeart(level)) {
                level.Frozen = false;
                level.PauseLock = false;
            }

            RoomTimerManager.Instance.ClearPbTimes(clearEndPoint);

            savedModSessions = null;
            savedLevel = null;
            savedEntities = null;
            savedCassetteBlockManager = null;

            state = States.None;
        }

        private bool IsAllowSave(Level level, Player player) {
            return !level.Paused && !level.Transitioning && !level.PauseLock && !level.InCutscene &&
                   !level.SkippingCutscene && player != null && !player.Dead && state != States.Loading &&
                   !disabledSaveStates.Contains(player.StateMachine.State) && IsNotCollectingHeart(level);
        }

        private bool IsNotCollectingHeart(Level level) {
            return !level.Entities.FindAll<HeartGem>().Any(heart => (bool) heart.GetFieldValue("collected"));
        }

        private void CheckButton(Level level, Player player) {
            if (GetVirtualButton(Mappings.Save).Pressed && IsAllowSave(level, player)) {
                GetVirtualButton(Mappings.Save).ConsumePress();
                SaveState(level);
            } else if (GetVirtualButton(Mappings.Load).Pressed && !level.Paused && state == States.None) {
                GetVirtualButton(Mappings.Load).ConsumePress();
                if (IsSaved) {
                    LoadState(level);
                } else if (!level.Frozen) {
                    level.Add(new MiniTextbox(DialogIds.DialogNotSaved));
                }
            } else if (GetVirtualButton(Mappings.Clear).Pressed && !level.Paused) {
                GetVirtualButton(Mappings.Clear).ConsumePress();
                ClearState();
                if (IsNotCollectingHeart(level)) {
                    level.Add(new MiniTextbox(DialogIds.DialogClear));
                }
            } else if (MInput.Keyboard.Check(Keys.F5)) {
                ClearState();
            } else if (GetVirtualButton(Mappings.SwitchAutoLoadState).Pressed && !level.Paused) {
                GetVirtualButton(Mappings.SwitchAutoLoadState).ConsumePress();
                Settings.AutoLoadAfterDeath = !Settings.AutoLoadAfterDeath;
                SpeedrunToolModule.Instance.SaveSettings();
            }
        }


        // @formatter:off
        private static readonly Lazy<StateManager> Lazy = new Lazy<StateManager>(() => new StateManager());
        public static StateManager Instance => Lazy.Value;
        private StateManager() { }
        // @formatter:on
    }

    internal static class DynDataUtils {
        private static object CreateDynData(object obj) {
            Type type = obj.GetType();
            string key = $"DynDataUtils-CreateDynData-{type.FullName}";

            ConstructorInfo constructorInfo = type.GetExtendedDataValue<ConstructorInfo>(key);

            if (constructorInfo == null) {
                constructorInfo = typeof(DynData<>).MakeGenericType(type).GetConstructor(new[] {type});
                type.SetExtendedDataValue(key, constructorInfo);
            }

            return constructorInfo?.Invoke(new[] {obj});
        }

        public static IDictionary GetDataMap(Type type) {
            string key = $"DynDataUtils-GetDataMap-{type}";

            FieldInfo fieldInfo = type.GetExtendedDataValue<FieldInfo>(key);

            if (fieldInfo == null) {
                fieldInfo = typeof(DynData<>).MakeGenericType(type).GetField("_DataMap", BindingFlags.Static | BindingFlags.NonPublic);
                type.SetExtendedDataValue(key, fieldInfo);
            }

            return fieldInfo?.GetValue(null) as IDictionary;
        }

        public static Dictionary<string, object> GetDate(object obj) {
            return CreateDynData(obj)?.GetPropertyValue("Data") as Dictionary<string, object>;
        }
    }

    internal class EntitiesWrapper {
        public readonly List<Entity> Entities;
        public readonly Dictionary<int, Dictionary<string, object>> DynDataDict;

        public EntitiesWrapper(List<Entity> entities, Dictionary<int, Dictionary<string, object>> dynDataDict) {
            Entities = entities;
            DynDataDict = dynDataDict;
        }
    }
}