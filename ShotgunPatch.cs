using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;

namespace BetterShotgun {
    class FadeOutLine : MonoBehaviour {
        private const float lifetime = 0.4f;
        private const float width = 0.02f;
        private static readonly Color col = new(1f, 0f, 0f);

        private float alive = 0f;
        private LineRenderer line;
        public Vector3 start, end;
        private static readonly Material mat = new(Shader.Find("Legacy Shaders/Particles/Alpha Blended Premultiply"));
        public void Prep() {
            var len = Vector3.Distance(start, end);
            var lenFrac = (NewShotgunHandler.range - len) / NewShotgunHandler.range;
            line = gameObject.AddComponent<LineRenderer>();
            line.startColor = col;
            line.endColor = col * lenFrac + Color.black * (1f - lenFrac);
            line.startWidth = width;
            line.endWidth = lenFrac * width;
            line.SetPositions(new Vector3[] { start, end });
            line.material = mat;
        }
        void Update() {
            alive += Time.deltaTime;
            if (alive >= lifetime) Destroy(gameObject);
            else {
                line.startColor = new Color(col.r, col.g, col.b, (lifetime - alive) / lifetime);
                line.endColor = new Color(line.endColor.r, line.endColor.g, line.endColor.b, (lifetime - alive) / lifetime);
            }
        }
    }

    public class Counter<T> {
        public T item;
        public int count;
    }
    public class CountHandler {
        public List<Counter<PlayerControllerB>> player = new();
        public List<Counter<EnemyAI>> enemy = new();
        public List<Counter<IHittable>> other = new();

        public void AddPlayerToCount(PlayerControllerB p) {
            if (player.Any(i => i.item == p)) player.First((i) => i.item == p).count++;
            else player.Add(new Counter<PlayerControllerB>(){item = p, count = 1});
        }
        public void AddEnemyToCount(EnemyAI ai) {
            if (enemy.Any(i => i.item == ai)) enemy.First((i) => i.item == ai).count++;
            else enemy.Add(new Counter<EnemyAI>(){item = ai, count = 1});
        }
        public void AddOtherToCount(IHittable hit) {
            if (other.Any(i => i.item == hit)) other.First((i) => i.item == hit).count++;
            else other.Add(new Counter<IHittable>(){item = hit, count = 1});
        }
    }

    class NewShotgunHandler {
        public const float range = 30f;

        static readonly int PLAYER_HIT_MASK = StartOfRound.Instance.collidersRoomMaskDefaultAndPlayers | 524288; //524288 = enemy mask
        static readonly int ENEMY_HIT_MASK = StartOfRound.Instance.collidersRoomMaskDefaultAndPlayers;

        public static System.Random ShotgunRandom = new(0);

        private static System.Collections.IEnumerator DelayedEarsRinging(float effectSeverity)
        {
            yield return new WaitForSeconds(0.6f);
            SoundManager.Instance.earsRingingTimer = effectSeverity;
        }

        private static void VisualiseShot(Vector3 start, Vector3 end) {
            GameObject trail = new("Trail Visual");
            FadeOutLine line = trail.AddComponent<FadeOutLine>();
            line.start = start;
            line.end = end;
            line.Prep();
        }

        public static void ShootGun(ShotgunItem gun, Vector3 shotgunPosition, Vector3 shotgunForward) {
            PlayerControllerB holder = gun.playerHeldBy;

            bool playerFired = gun.isHeld && gun.playerHeldBy != null;
            if (playerFired) {
                // correct offset to something more reasonable when a player fires
                shotgunPosition += GameNetworkManager.Instance.localPlayerController.gameplayCamera.transform.up * 0.25f; // vanilla code is -0.45
            }
            bool thisPlayerFired = playerFired && gun.playerHeldBy == GameNetworkManager.Instance.localPlayerController;
            if (thisPlayerFired) gun.playerHeldBy.playerBodyAnimator.SetTrigger("ShootShotgun");

            // fire and reduce shell count - copied from vanilla

            RoundManager.PlayRandomClip(gun.gunShootAudio, gun.gunShootSFX, randomize: true, 1f, 1840);
            WalkieTalkie.TransmitOneShotAudio(gun.gunShootAudio, gun.gunShootSFX[0]);
            gun.gunShootParticle.Play(withChildren: true);

            gun.isReloading = false;
            gun.shellsLoaded = Mathf.Clamp(gun.shellsLoaded - 1, 0, 2);

            PlayerControllerB localPlayerController = GameNetworkManager.Instance.localPlayerController;
            if (localPlayerController == null) return;

            // generic firing stuff - replaced with pellets

            // generate pellet vectors (done separately to minimise time random state is modified)
            var vectorList = new Vector3[ShotgunConfig.numTightPellets + ShotgunConfig.numLoosePellets];
            var oldRandomState = UnityEngine.Random.state;
            UnityEngine.Random.InitState(ShotgunRandom.Next());
            for (int i = 0; i < ShotgunConfig.numTightPellets + ShotgunConfig.numLoosePellets; i++) {
                float variance = (i < ShotgunConfig.numTightPellets) ? ShotgunConfig.tightPelletAngle : ShotgunConfig.loosePelletAngle;
                var circlePoint = UnityEngine.Random.onUnitSphere; // pick a random point on a sphere
                var angle = variance * Mathf.Sqrt(UnityEngine.Random.value); // pick a random angle to spread by
                if (Vector3.Angle(shotgunForward, circlePoint) < angle) circlePoint *= -1; // make sure the spread will be by the specified angle amount
                var vect = Vector3.RotateTowards(shotgunForward, circlePoint, angle * Mathf.PI / 180f, 0f); // rotate towards that random point, capped by chosen angle
                vectorList[i] = vect;
            }
            UnityEngine.Random.state = oldRandomState;

            // calculate ear ring and shake based on distance to gun
            float distance = Vector3.Distance(localPlayerController.transform.position, gun.shotgunRayPoint.transform.position);
            float earRingSeverity = 0f;
            if (distance < 5f) {
                earRingSeverity = 0.8f;
                HUDManager.Instance.ShakeCamera(ScreenShakeType.Big);
            }
            else if (distance < 15f) {
                earRingSeverity = 0.5f;
                HUDManager.Instance.ShakeCamera(ScreenShakeType.Big);
            }
            else if (distance < 23f) HUDManager.Instance.ShakeCamera(ScreenShakeType.Small);

            if (earRingSeverity > 0f && SoundManager.Instance.timeSinceEarsStartedRinging > 16f && !playerFired)
                gun.StartCoroutine(DelayedEarsRinging(earRingSeverity));

            // raycast those vectors to find hits
            Ray ray;
            var counts = new CountHandler();
            // TODO: modify count tracker to handle distance pellets travel? sqrt(1-dist/range) seems reasonable for damage worth
            for (int i = 0; i < vectorList.Length; i++) {
                Vector3 vect = vectorList[i];
                ray = new Ray(shotgunPosition, vect);
                RaycastHit[] hits = Physics.RaycastAll(ray, range, playerFired ? PLAYER_HIT_MASK : ENEMY_HIT_MASK, QueryTriggerInteraction.Collide);
                Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
                Vector3 end = shotgunPosition + vect * range;
                Debug.Log("SHOTGUN: RaycastAll hit " + hits.Length + " things (" + playerFired + "," + thisPlayerFired + ")");
                for (int j = 0; j < hits.Length; j++) {
                    GameObject obj = hits[j].transform.gameObject;
                    if (obj.TryGetComponent(out IHittable hittable)) {
                        if (ReferenceEquals(hittable, gun.playerHeldBy)) continue; // self hit
                        EnemyAI ai = null;
                        if (hittable is EnemyAICollisionDetect detect) ai = detect.mainScript;
                        if (ai != null) {
                            if (!playerFired) continue; // enemy hit enemy
                            if (ai.isEnemyDead || ai.enemyHP <= 0 || !ai.enemyType.canDie) continue; // skip dead things
                        }
                        if (hittable is PlayerControllerB) counts.AddPlayerToCount(hittable as PlayerControllerB);
                        else if (ai != null) counts.AddEnemyToCount(ai);
                        else if (playerFired) counts.AddOtherToCount(hittable);
                        else continue; // enemy hit something else (webs?)
                        end = hits[j].point;
                        Debug.Log("SHOTGUN: Hit [" + hittable + "] (" + (j+1) + "@" + Vector3.Distance(shotgunPosition, end) + ")");
                        break;
                    }
                    else {
                        // precaution: hit enemy without hitting hittable (immune to shovels?)
                        if (hits[j].collider.TryGetComponent(out EnemyAI ai)) {
                            if (playerFired && !ai.isEnemyDead && ai.enemyHP > 0 && ai.enemyType.canDie) {
                                counts.AddEnemyToCount(ai);
                                end = hits[j].point;
                                Debug.Log("SHOTGUN: Backup hit [" + ai + "] (" + (j+1) + "@" + Vector3.Distance(shotgunPosition, end) + ")");
                                break;
                            }
                            else continue;
                        }
                        end = hits[j].point;
                        Debug.Log("SHOTGUN: Wall [" + obj + "] (" + (j+1) + "@" + Vector3.Distance(shotgunPosition, end) + ")");
                        break; // wall or other obstruction
                    }
                }
                VisualiseShot(shotgunPosition, end);
            }

            // deal damage all at once - prevents piercing alive and reduces damage calls
            counts.player.ForEach(p => {
                // grouping player damage also ensures strong hits (3+ pellets) ignore critical damage - 5 is always lethal rather than being critical
                int damage = p.count * 20;
                Debug.Log("SHOTGUN: Hit " + p.item + " with " + p.count + " pellets for " + damage + " damage");
                p.item.DamagePlayer(damage, true, true, CauseOfDeath.Gunshots, 0, false, shotgunForward);
            });
            counts.enemy.ForEach(e => {
                // doing 1:1 damage is too strong, but one pellet should always do damage
                int damage = e.count / 2 + 1; // half rounded down plus one (1,2,2,3,3,4,4,5,5,6)
                Debug.Log("SHOTGUN: Hit " + e.item + " with " + e.count + " pellets for " + damage + " damage");
                e.item.HitEnemy(damage, gun.playerHeldBy, true);
            });
            counts.other.ForEach(o => {
                int damage = o.count / 2 + 1;
                Debug.Log("SHOTGUN: Hit " + o.item + " with " + o.count + " pellets for " + damage + " damage");
                o.item.Hit(damage, shotgunForward, gun.playerHeldBy, true);
            });

            ray = new Ray(shotgunPosition, shotgunForward);
            if (Physics.Raycast(ray, out RaycastHit hitInfo, range, StartOfRound.Instance.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore))
            {
                gun.gunBulletsRicochetAudio.transform.position = ray.GetPoint(hitInfo.distance - 0.5f);
                gun.gunBulletsRicochetAudio.Play();
            }
        }
    }

    class ShotgunPatch {
        [HarmonyPatch(typeof(ShotgunItem), "ShootGun")]
        [HarmonyPrefix]
        static bool ReplaceShotgunCode(ShotgunItem __instance, Vector3 shotgunPosition, Vector3 shotgunForward) {
            NewShotgunHandler.ShootGun(__instance, shotgunPosition, shotgunForward);
            return false;
        }

        [HarmonyPatch(typeof(StartOfRound), "ChooseNewRandomMapSeed")]
        [HarmonyPostfix]
        static void UpdateShotgunSeed(StartOfRound __instance) {
            // TODO: this should be networked
            NewShotgunHandler.ShotgunRandom = new System.Random(__instance.randomMapSeed);
            Debug.Log("Shotgun seed: " + __instance.randomMapSeed);
        }

        [HarmonyPatch(typeof(SandSpiderAI), "HitEnemy")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> SpiderDamageFix(IEnumerable<CodeInstruction> original) {
            var c = -1;
            var fieldInfo = typeof(SandSpiderAI).GetField("health", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            foreach (CodeInstruction inst in original) {
                if (c == -1 && inst.LoadsField(fieldInfo)) {
                    c = 0;
                    yield return inst;
                }
                else if (c == 0) {
                    c = 1;
                    // use the force value and not a static 1
                    var newInst = new CodeInstruction(OpCodes.Ldarg_1);
                    yield return newInst;
                }
                else yield return inst;
            }
        }
    }
}