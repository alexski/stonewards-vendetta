using System;
using UnityEngine;

namespace Vendetta.Game
{
    /// <summary>
    /// Moves one monster through a leap: crouch (the telegraph), an arc through the air, then a short
    /// landing. The game's monsters have no jump animation, so this moves, tilts and stretches the model.
    /// </summary>
    public class LeapMotion
    {
        private enum Phase { Crouch, Air, Land }

        private const float LandSeconds = 0.6f; // it can't act for this long after landing
        private const float CrouchHeight = 0.7f, AirHeight = 1.12f;

        public readonly Component C;
        public bool Done { get; private set; }
        public bool InAir { get { return !Done && _phase == Phase.Air; } }
        /// <summary>In the air or just landed. Crouching doesn't count: it can still finish an attack.</summary>
        public bool Moving { get { return !Done && _phase != Phase.Crouch; } }

        private readonly Vector3 _scale;
        private readonly Component _agent, _body;
        private readonly Func<bool> _alive;
        private readonly Action<Vector3> _placed;
        private Phase _phase;
        private float _time, _length, _peak, _yaw;
        private Vector3 _from, _to;
        private bool _wasKinematic;

        public LeapMotion(Component monster, float crouchSeconds, Func<bool> alive, Action<Vector3> placed)
        {
            C = monster;
            _alive = alive;
            _placed = placed;
            _scale = monster.transform.localScale;
            _agent = Get("UnityEngine.AI.NavMeshAgent");
            _body = Get("UnityEngine.Rigidbody");
            _length = Math.Max(0.05f, crouchSeconds);
        }

        private Component Get(string type)
        {
            var t = Reflect.FindType(type);
            return t != null ? C.GetComponent(t) : null;
        }

        /// <summary>The game switches the agent off while a monster is being knocked back.</summary>
        public bool CanJump { get { return !_agent || Reflect.GetBool(_agent, "enabled", true); } }

        public void Jump(Vector3 to)
        {
            _phase = Phase.Air;
            _time = 0f;
            _from = C.transform.position;
            _to = to;
            float dx = to.x - _from.x, dz = to.z - _from.z;
            float distance = (float)Math.Sqrt(dx * dx + dz * dz);
            _yaw = (float)(Math.Atan2(dx, dz) * 180 / Math.PI);
            _peak = Math.Max(_from.y, to.y) + 2.5f + distance * 0.08f;
            _length = Math.Min(1.5f, Math.Max(0.75f, 0.7f + distance * 0.02f));
            if (_agent) Reflect.Set(_agent, "enabled", false); // or it pulls the monster back to the ground
            if (_body)
            {
                _wasKinematic = Reflect.GetBool(_body, "isKinematic", true);
                Reflect.Set(_body, "isKinematic", true);
            }
        }

        public void Cancel()
        {
            if (Done || _phase == Phase.Air) return; // can't stop in mid air
            if (C) C.transform.localScale = _scale;
            Done = true;
        }

        public void Tick(float dt)
        {
            if (Done) return;
            if (!C) { Done = true; return; }
            _time += dt;
            float t = Math.Min(1f, _time / _length);
            switch (_phase)
            {
                case Phase.Crouch:
                    if (!_alive() || _time > _length + 3f) { Cancel(); return; } // nobody told it to jump
                    Stretch(1f + (CrouchHeight - 1f) * t);
                    break;
                case Phase.Air:
                    C.transform.position = PointAt(_from, _to, _peak, t);
                    C.transform.rotation = Quaternion.Euler(-25f + 50f * t, _yaw, 0f); // nose up, then down
                    Stretch(AirHeight);
                    if (t >= 1f) Land();
                    break;
                case Phase.Land:
                    Stretch(CrouchHeight + (1f - CrouchHeight) * t);
                    if (t >= 1f) { C.transform.localScale = _scale; Done = true; }
                    break;
            }
        }

        private void Land()
        {
            C.transform.position = _to;
            C.transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
            if (_body) Reflect.Set(_body, "isKinematic", _wasKinematic);
            if (!_alive()) { C.transform.localScale = _scale; Done = true; return; }
            if (_agent) Reflect.Set(_agent, "enabled", true);
            _placed(_to);
            _phase = Phase.Land;
            _time = 0f;
            _length = LandSeconds;
        }

        private void Stretch(float height)
        {
            float width = 1f + (1f - height) * 0.5f; // squashed is wider, stretched is thinner
            C.transform.localScale = new Vector3(_scale.x * width, _scale.y * height, _scale.z * width);
        }

        /// <summary>Goes up steeply before it goes across, to clear the wall it's standing against. t is 0..1.</summary>
        public static Vector3 PointAt(Vector3 from, Vector3 to, float peak, float t)
        {
            const float top = 0.45f; // when it's highest
            float late = (float)Math.Pow(t, 1.6);
            float across = late * late * (3f - 2f * late);
            float fall = t < top ? 1f - t / top : (t - top) / (1f - top);
            float y = peak - (peak - (t < top ? from.y : to.y)) * fall * fall;
            return new Vector3(from.x + (to.x - from.x) * across, y, from.z + (to.z - from.z) * across);
        }
    }
}
