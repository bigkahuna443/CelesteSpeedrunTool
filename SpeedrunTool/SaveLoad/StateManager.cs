using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Celeste.Mod.SpeedrunTool.DeathStatistics;
using Celeste.Mod.SpeedrunTool.Extensions;
using Celeste.Mod.SpeedrunTool.RoomTimer;
using FMOD.Studio;
using Force.DeepCloner;
using Force.DeepCloner.Helpers;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Monocle;
using static Celeste.Mod.SpeedrunTool.ButtonConfigUi;

namespace Celeste.Mod.SpeedrunTool.SaveLoad {
    public sealed class StateManager {
        private static SpeedrunToolSettings Settings => SpeedrunToolModule.Settings;

        private static readonly List<int> DisabledSaveStates = new List<int> {
            Player.StReflectionFall,
            Player.StTempleFall,
            Player.StCassetteFly,
            Player.StIntroJump,
            Player.StIntroWalk,
            Player.StIntroRespawn,
            Player.StIntroWakeUp,
        };

        private Level savedLevel;
        private bool IsSaved => savedLevel != null;
        private List<Entity> savedEntities;

        private Task<DeepCloneState> preCloneTask;

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
            Waiting,
        }

        private readonly HashSet<EventInstance> playingEventInstances = new HashSet<EventInstance>();

        #region Hook

        public void OnLoad() {
            DeepClonerUtils.Config();
            SaveLoadAction.OnLoad();
            EventInstanceUtils.OnHook();
            StateMarkUtils.OnLoad();
            On.Celeste.Level.Update += CheckButtonsAndUpdateBackdrop;
            On.Monocle.Scene.Begin += ClearStateWhenSwitchScene;
            On.Celeste.PlayerDeadBody.End += AutoLoadStateWhenDeath;
        }

        public void OnUnload() {
            DeepClonerUtils.Clear();
            SaveLoadAction.OnUnload();
            EventInstanceUtils.OnUnhook();
            StateMarkUtils.OnUnload();
            On.Celeste.Level.Update -= CheckButtonsAndUpdateBackdrop;
            On.Monocle.Scene.Begin -= ClearStateWhenSwitchScene;
            On.Celeste.PlayerDeadBody.End -= AutoLoadStateWhenDeath;
        }

        private void CheckButtonsAndUpdateBackdrop(On.Celeste.Level.orig_Update orig, Level self) {
            orig(self);
            CheckButton(self);

            if (state == States.Waiting && self.Frozen) {
                self.Foreground.Update(self);
                self.Background.Update(self);
            }
        }

        private void ClearStateWhenSwitchScene(On.Monocle.Scene.orig_Begin orig, Scene self) {
            orig(self);
            if (self is Overworld) ClearState();
            if (IsSaved) {
                if (self is Level) {
                    state = States.None; // 修复：读档途中按下 PageDown/Up 后无法存档
                    PreCloneEntities(savedEntities);
                }

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
                && Engine.Scene is Level level
            ) {
                level.OnEndOfFrame += () => LoadState(false);
                self.RemoveSelf();
            } else {
                orig(self);
            }
        }

        #endregion Hook

        // public for TAS Mod
        // ReSharper disable once UnusedMember.Global
        public bool SaveState() {
            return SaveState(true);
        }

        private bool SaveState(bool tas) {
            if (!(Engine.Scene is Level level)) return false;
            if (!IsAllowSave(level, level.GetPlayer())) return false;

            ClearState(false);

            savedLevel = level.ShallowClone();
            savedLevel.Lighting = level.Lighting.ShallowClone();
            savedLevel.FormationBackdrop = level.FormationBackdrop.ShallowClone();
            savedLevel.Session = level.Session.DeepCloneShared();
            savedLevel.Camera = level.Camera.DeepCloneShared();
            savedLevel.Bloom = level.Bloom.DeepCloneShared();
            savedLevel.Background = level.Background.DeepCloneShared();
            savedLevel.Foreground = level.Foreground.DeepCloneShared();

            savedEntities = GetEntitiesNeedDeepClone(level).DeepCloneShared();

            // External
            savedFreezeTimer = Engine.FreezeTimer;
            savedTimeRate = Engine.TimeRate;
            savedGlitchValue = Glitch.Value;
            savedDistortAnxiety = Distort.Anxiety;
            savedDistortGameRate = Distort.GameRate;

            // Mod 和其他
            SaveLoadAction.OnSaveState(level);

            // save all mod sessions
            savedModSessions = new Dictionary<EverestModule, EverestModuleSession>();
            foreach (EverestModule module in Everest.Modules) {
                if (module._Session != null) {
                    savedModSessions[module] = module._Session.DeepCloneYaml(module.SessionType);
                }
            }

            DeepClonerUtils.ClearSharedDeepCloneState();
            return LoadState(tas);
        }

        // public for TAS Mod
        // ReSharper disable once UnusedMember.Global
        public bool LoadState() {
            return LoadState(true);
        }

        private bool LoadState(bool tas) {
            if (!(Engine.Scene is Level level)) return false;
            if (level.Paused || state != States.None || !IsSaved) return false;

            state = States.Loading;
            DeepClonerUtils.SetSharedDeepCloneState(preCloneTask?.Result);

            // 修复问题：死亡瞬间读档 PlayerDeadBody 没被清除，导致读档完毕后 madeline 自动 retry
            level.Entities.UpdateLists();

            // External
            RoomTimerManager.Instance.ResetTime();
            DeathStatisticsManager.Instance.Died = false;

            level.SetFieldValue("transition", null); // 允许切换房间时读档  // Allow reading fields when switching rooms
            level.Displacement.Clear(); // 避免冲刺后读档残留爆破效果  // Remove dash displacement effect
            level.ParticlesBG.Clear();
            level.Particles.Clear();
            level.ParticlesFG.Clear();
            TrailManager.Clear(); // 清除冲刺的残影  // Remove dash trail

            UnloadLevelEntities(level);
            RestoreLevelEntities(level);
            RestoreCassetteBlockManager1(level); // 停止播放主音乐，等待播放节奏音乐
            RestoreLevel(level);

            // AddSaveStateMark(level);

            // Mod 和其他
            SaveLoadAction.OnLoadState(level);

            // restore all mod sessions
            foreach (EverestModule module in Everest.Modules) {
                if (savedModSessions.TryGetValue(module, out EverestModuleSession savedModSession)) {
                    module._Session = savedModSession.DeepCloneYaml(module.SessionType);
                }
            }

            // 修复问题：未打开自动读档时，死掉按下确认键后读档完成会接着执行 Reload 复活方法
            // Fix: When AutoLoadStateAfterDeath is off, if manually LoadState() after death, level.Reload() will still be executed.
            ClearScreenWipe(level);

            level.Frozen = true; // 加一个转场等待，避免太突兀   // Add a pause to avoid being too abrupt
            level.TimerStopped = true; // 停止计时器  // Stop timer

            level.DoScreenWipe(true, () => {
                // 修复问题：死亡后出现黑屏的一瞬间手动读档后游戏崩溃，因为 ScreenWipe 执行了 level.Reload() 方法
                // System.NullReferenceException: 未将对象引用设置到对象的实例。
                // 在 Celeste.CameraTargetTrigger.OnLeave(Player player)
                // 在 Celeste.Player.Removed(Scene scene)
                ClearScreenWipe(level);

                if (!tas && Settings.FreezeAfterLoadState) {
                    state = States.Waiting;
                    level.PauseLock = true;
                } else {
                    LoadStateComplete(level);
                }
            });

            return true;
        }

        private void LoadStateComplete(Level level) {
            RestoreLevel(level);
            RestoreCassetteBlockManager2(level);
            RoomTimerManager.Instance.SavedEndPoint?.ReadyForTime();
            foreach (EventInstance instance in playingEventInstances) instance.start();
            playingEventInstances.Clear();
            DeepClonerUtils.ClearSharedDeepCloneState();
            state = States.None;
        }

        private void ClearScreenWipe(Level level) {
            level.RendererList.Renderers.ForEach(renderer => {
                if (renderer is ScreenWipe wipe) wipe.Cancel();
            });
        }

        // 分两步的原因是更早的停止音乐，听起来更舒服更好一点
        private void RestoreCassetteBlockManager1(Level level) {
            if (level.Entities.FindFirst<CassetteBlockManager>() is CassetteBlockManager manager) {
                if (manager.GetFieldValue("snapshot") is EventInstance snapshot) {
                    snapshot.start();
                }
            }
        }

        private void RestoreCassetteBlockManager2(Level level) {
            if (level.Entities.FindFirst<CassetteBlockManager>() is CassetteBlockManager manager) {
                if (manager.GetFieldValue("sfx") is EventInstance sfx &&
                    !(bool) manager.GetFieldValue("isLevelMusic")) {
                    if ((int) manager.GetFieldValue("leadBeats") <= 0) {
                        sfx.start();
                    }
                }
            }
        }

        private void ClearState(bool clearEndPoint = true) {
            if (Engine.Scene is Level level && IsNotCollectingHeart(level) && !level.Completed) {
                level.Frozen = false;
                level.PauseLock = false;
            }

            try {
                RoomTimerManager.Instance.ClearPbTimes(clearEndPoint);
            } catch (NullReferenceException) {
                // Don't know why it happened
                // SpeedrunToolModule.Instance = null Exception
            }

            playingEventInstances.Clear();

            savedModSessions = null;
            savedLevel = null;
            savedEntities = null;

            preCloneTask = null;

            DeepClonerUtils.ClearSharedDeepCloneState();

            // Mod
            SaveLoadAction.OnClearState();

            state = States.None;
        }

        private void UnloadLevelEntities(Level level) {
            List<Entity> entities = GetEntitiesExcludingGlobal(level);
            level.Remove(entities);
            level.Entities.UpdateLists();
            // 修复：Retry 后读档依然执行 PlayerDeadBody.End 的问题
            // 由 level.CopyAllSimpleTypeFields(savedLevel) 自动处理了
            // level.RetryPlayerCorpse = null;
        }

        private void PreCloneEntities(List<Entity> entities) {
            preCloneTask = Task.Factory.StartNew(() => {
                DeepCloneState deepCloneState = new DeepCloneState();
                entities.DeepClone(deepCloneState);
                return deepCloneState;
            });
        }

        private void RestoreLevelEntities(Level level) {
            PreCloneEntities(savedEntities);
            List<Entity> deepCloneEntities = savedEntities.DeepCloneShared();

            // just follow the level.LoadLevel add player last. There must some black magic in it.
            // fixed: Player can't perform a really spike jump when wind blow down.
            if (deepCloneEntities.FirstOrDefault(entity => entity is Player) is Player player) {
                deepCloneEntities.Remove(player);
                deepCloneEntities.Add(player);
            }

            // Re Add Entities
            List<Entity> entities = (List<Entity>) level.Entities.GetFieldValue("entities");
            HashSet<Entity> current = (HashSet<Entity>) level.Entities.GetFieldValue("current");
            foreach (Entity entity in deepCloneEntities) {
                if (entities.Contains(entity)) continue;

                current.Add(entity);
                entities.Add(entity);

                level.TagLists.InvokeMethod("EntityAdded", entity);
                level.Tracker.InvokeMethod("EntityAdded", entity);
                entity.Components?.ToList()
                    .ForEach(component => {
                        level.Tracker.InvokeMethod("ComponentAdded", component);

                        // 等 ScreenWipe 完毕再重新播放
                        if (component is SoundSource source && source.Playing &&
                            source.GetFieldValue("instance") is EventInstance eventInstance) {
                            playingEventInstances.Add(eventInstance);
                        }
                    });
                level.InvokeMethod("SetActualDepth", entity);
                Dictionary<Type, Queue<Entity>> pools = (Dictionary<Type, Queue<Entity>>) Engine.Pooler.GetPropertyValue("Pools");
                Type type = entity.GetType();
                if (pools.ContainsKey(type) && pools[type].Count > 0) {
                    pools[type].Dequeue();
                }
            }
        }

        private void RestoreLevel(Level level) {
            level.Camera.CopyFrom(savedLevel.Camera);
            savedLevel.Session.DeepCloneToShared(level.Session);
            savedLevel.Bloom.DeepCloneToShared(level.Bloom);
            savedLevel.Background.DeepCloneToShared(level.Background);
            savedLevel.Foreground.DeepCloneToShared(level.Foreground);
            level.CopyAllSimpleTypeFieldsAndNull(savedLevel);
            level.Lighting.CopyAllSimpleTypeFieldsAndNull(savedLevel.Lighting);
            level.FormationBackdrop.CopyAllSimpleTypeFieldsAndNull(savedLevel.FormationBackdrop);

            // External Static Field
            Engine.FreezeTimer = savedFreezeTimer;
            Engine.TimeRate = savedTimeRate;
            Glitch.Value = savedGlitchValue;
            Distort.Anxiety = savedDistortAnxiety;
            Distort.GameRate = savedDistortGameRate;
        }

        private List<Entity> GetEntitiesExcludingGlobal(Level level) {
            var result = level.Entities.Where(
                entity => !entity.TagCheck(Tags.Global) || entity is CassetteBlockManager).ToList();

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

            if (level.Entities.FirstOrDefault(entity =>
                    entity.GetType().FullName == "Celeste.Mod.AcidHelper.Entities.InstantTeleporterRenderer") is Entity
                teleporterRenderer) {
                result.Add(teleporterRenderer);
            }

            return result;
        }

        private List<Entity> GetEntitiesNeedDeepClone(Level level) {
            return GetEntitiesExcludingGlobal(level).Where(entity => {
                // 不恢复设置了 IgnoreSaveLoadComponent 的物体
                // SpeedrunTool 里有 ConfettiRenderer 和一些 MiniTextbox
                if (entity.IsIgnoreSaveLoad()) return false;

                // 不恢复 CelesteNet/CelesteTAS 的物体
                // Do not restore CelesteNet/CelesteTAS objects
                if (entity.GetType().FullName is string name &&
                    (name.StartsWith("Celeste.Mod.CelesteNet.") || name.StartsWith("TAS.")))
                    return false;

                return true;
            }).ToList();
        }

        private bool IsAllowSave(Level level, Player player) {
            return state == States.None
                   && !level.Paused && !level.Transitioning && !level.InCutscene && !level.SkippingCutscene
                   && player != null && !player.Dead && !DisabledSaveStates.Contains(player.StateMachine.State)
                   && IsNotCollectingHeart(level);
        }

        private bool IsNotCollectingHeart(Level level) {
            return !level.Entities.FindAll<HeartGem>().Any(heart => (bool) heart.GetFieldValue("collected"));
        }

        private void CheckButton(Level level) {
            if (!SpeedrunToolModule.Enabled) return;

            if (GetVirtualButton(Mappings.Save).Pressed) {
                GetVirtualButton(Mappings.Save).ConsumePress();
                SaveState(false);
            } else if (GetVirtualButton(Mappings.Load).Pressed && !level.Paused && state == States.None) {
                GetVirtualButton(Mappings.Load).ConsumePress();
                if (IsSaved) {
                    LoadState(false);
                } else if (!level.Frozen) {
                    level.Add(new MiniTextbox(DialogIds.DialogNotSaved).IgnoreSaveLoad());
                }
            } else if (GetVirtualButton(Mappings.Clear).Pressed && !level.Paused && state == States.None) {
                GetVirtualButton(Mappings.Clear).ConsumePress();
                ClearState();
                if (IsNotCollectingHeart(level) && !level.Completed) {
                    level.Add(new MiniTextbox(DialogIds.DialogClear).IgnoreSaveLoad());
                }
            } else if (MInput.Keyboard.Check(Keys.F5)) {
                ClearState();
            } else if (GetVirtualButton(Mappings.SwitchAutoLoadState).Pressed && !level.Paused) {
                GetVirtualButton(Mappings.SwitchAutoLoadState).ConsumePress();
                Settings.AutoLoadAfterDeath = !Settings.AutoLoadAfterDeath;
                SpeedrunToolModule.Instance.SaveSettings();
            } else if (state == States.Waiting && !level.Paused
                                               && (Input.Dash.Pressed
                                                   || Input.Grab.Check
                                                   || Input.Jump.Check
                                                   || Input.Pause.Check
                                                   || Input.Talk.Check
                                                   || Input.MoveX != 0
                                                   || Input.MoveY != 0
                                                   || Input.Aim.Value != Vector2.Zero
                                                   || GetVirtualButton(Mappings.Load).Released
                                               )) {
                LoadStateComplete(level);
            }
        }


        // @formatter:off
        private static readonly Lazy<StateManager> Lazy = new Lazy<StateManager>(() => new StateManager());
        public static StateManager Instance => Lazy.Value;

        private StateManager() { }
        // @formatter:on
    }
}