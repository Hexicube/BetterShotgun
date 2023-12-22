using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace BetterShotgun {
    class FadeOutLine : MonoBehaviour {
        private const float lifetime = 0.4f;
        private const float width = 0.02f;
        private static readonly Color col = new Color(1f, 0f, 0f);

        private float alive = 0f;
        private LineRenderer line;
        public Vector3 start, end;
        private static Material mat = new Material(Shader.Find("Legacy Shaders/Particles/Alpha Blended Premultiply"));
        public void Prep() {
            var len = Vector3.Distance(start, end);
            var lenFrac = (30f - len) / 30f;
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
            if (alive >= lifetime) Object.Destroy(gameObject);
            else {
                line.startColor = new Color(col.r, col.g, col.b, (lifetime - alive) / lifetime);
                line.endColor = new Color(line.endColor.r, line.endColor.g, line.endColor.b, (lifetime - alive) / lifetime);
            }
        }
    }

    class NewShotgunHandler {
        public static System.Random ShotgunRandom = new System.Random(0);

        private static System.Collections.IEnumerator delayedEarsRinging(float effectSeverity)
        {
            yield return new WaitForSeconds(0.6f);
            SoundManager.Instance.earsRingingTimer = effectSeverity;
        }

        private static void VisualiseShot(Vector3 start, Vector3 end) {
            var len = Vector3.Distance(start, end);
            GameObject trail = new GameObject("Trail Visual");
            FadeOutLine line = trail.AddComponent<FadeOutLine>();
            line.start = start;
            line.end = end;
            line.Prep();
        }

        public static void ShootGun(ShotgunItem gun, Vector3 shotgunPosition, Vector3 shotgunForward) {
            PlayerControllerB holder = gun.playerHeldBy;

            bool playerFired = gun.isHeld && (Object)(object)gun.playerHeldBy != null;
            if (playerFired) {
                // correct offset to something more reasonable when a player fires
                shotgunPosition += GameNetworkManager.Instance.localPlayerController.gameplayCamera.transform.up * 0.25f; // vanilla code is -0.45
            }
            bool thisPlayerFired = playerFired && (Object)(object)gun.playerHeldBy == (Object)(object)GameNetworkManager.Instance.localPlayerController;
            if (thisPlayerFired) gun.playerHeldBy.playerBodyAnimator.SetTrigger("ShootShotgun");

            // forcing some pellets into a tighter packing should feel more consistent
            const int numTightPellets = 3;
            const float tightPelletAngle = 2.5f;
            const int numLoosePellets = 7;
            const float loosePelletAngle = 10f;

            // fire and reduce shell count - copied from vanilla

            RoundManager.PlayRandomClip(gun.gunShootAudio, gun.gunShootSFX, randomize: true, 1f, 1840);
            WalkieTalkie.TransmitOneShotAudio(gun.gunShootAudio, gun.gunShootSFX[0]);
            gun.gunShootParticle.Play(withChildren: true);

            gun.isReloading = false;
            gun.shellsLoaded = Mathf.Clamp(gun.shellsLoaded - 1, 0, 2);

            PlayerControllerB localPlayerController = GameNetworkManager.Instance.localPlayerController;
            if ((Object)(object)localPlayerController == null) return;

            // generic firing stuff - replaced with pellets

            // generate pellet vectors (done separately to minimise time random state is modified)
            var vectorList = new Vector3[numTightPellets + numLoosePellets];
            var oldRandomState = Random.state;
            Random.InitState(ShotgunRandom.Next());
            for (int i = 0; i < numTightPellets + numLoosePellets; i++) {
                float variance = (i < numTightPellets) ? tightPelletAngle : loosePelletAngle;
                var circlePoint = Random.onUnitSphere; // pick a random point on a sphere
                var angle = variance * Mathf.Sqrt(Random.value); // pick a random angle to spread by
                var vect = Vector3.RotateTowards(shotgunForward, circlePoint, angle * Mathf.PI / 180f, 0f); // rotate towards that random point, capped by chosen angle
                vectorList[i] = vect;
            }
            Random.state = oldRandomState;

            // calculate ear ring and shake based on distance to gun
            float distance = Vector3.Distance(((Component)(object)localPlayerController).transform.position, gun.shotgunRayPoint.transform.position);
            float earRingSeverity = 0f;
            if (distance < 5f)
            {
                earRingSeverity = 0.8f;
                HUDManager.Instance.ShakeCamera(ScreenShakeType.Big);
            }
            else if (distance < 15f)
            {
                earRingSeverity = 0.5f;
                HUDManager.Instance.ShakeCamera(ScreenShakeType.Big);
            }
            else if (distance < 23f)
            {
                HUDManager.Instance.ShakeCamera(ScreenShakeType.Small);
            }
            if (earRingSeverity > 0f && SoundManager.Instance.timeSinceEarsStartedRinging > 16f && !playerFired)
            {
                ((MonoBehaviour)(object)gun).StartCoroutine(delayedEarsRinging(earRingSeverity));
            }

            // raycast those vectors to find hits
            Ray ray;
            RaycastHit hitInfo;
            IHittable hittable;
            // TODO: separate hit detection into player vs enemy to simplify code and have better masks
            for (int i = 0; i < vectorList.Length; i++) {
                Vector3 vect = vectorList[i];
                ray = new Ray(shotgunPosition, vect);
                RaycastHit[] hits = Physics.RaycastAll(ray, 30f, StartOfRound.Instance.collidersRoomMaskDefaultAndPlayers | 524288, QueryTriggerInteraction.Collide); //524288 = enemy mask
                System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
                Vector3 end = shotgunPosition + vect * 30f;
                Debug.Log("SHOTGUN: RaycastAll hit " + hits.Length + " things (" + playerFired + "," + thisPlayerFired + ")");
                for (int j = 0; j < hits.Length; j++) {
                    GameObject obj = hits[j].transform.gameObject;
                    if (obj.TryGetComponent<IHittable>(out hittable)) {
                        if (hittable == gun.playerHeldBy) continue; // self hit
                        EnemyAI ai = null;
                        if (hittable is EnemyAICollisionDetect) ai = ((EnemyAICollisionDetect)hittable).mainScript;
                        if (ai != null) {
                            if (!playerFired) continue; // enemy hit enemy
                            if (ai.isEnemyDead || ai.enemyHP <= 0 || !ai.enemyType.canDie) continue; // skip dead things
                        }
                        if (hittable is PlayerControllerB) ((PlayerControllerB)hittable).DamagePlayer(20, true, true, CauseOfDeath.Gunshots, 0, false, vect); // hit player
                        else hittable.Hit(1, vect, gun.playerHeldBy, true); // hit enemy
                        end = hits[j].point;
                        Debug.Log("SHOTGUN: Hit [" + hittable + "] (" + (j+1) + "@" + Vector3.Distance(shotgunPosition, end) + ")");
                        break;
                    }
                    else {
                        // precaution: hit enemy without hitting hittable
                        if (hits[j].collider.TryGetComponent<EnemyAI>(out var ai)) {
                            if (playerFired && !ai.isEnemyDead && ai.enemyHP > 0 && ai.enemyType.canDie) {
                                ai.HitEnemy(1, gun.playerHeldBy, true); // player hit enemy
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

            ray = new Ray(shotgunPosition, shotgunForward);
            if (Physics.Raycast(ray, out hitInfo, 30f, StartOfRound.Instance.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore))
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
            NewShotgunHandler.ShotgunRandom = new System.Random(__instance.randomMapSeed);
            Debug.Log("Shotgun seed: " + __instance.randomMapSeed);
        }
    }
}