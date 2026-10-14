#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace FIMSpace.FProceduralAnimation
{
    [CreateAssetMenu(fileName = "Module Settings-Impulses On Stop And On Land", menuName = "FImpossible Creations/Legs Animator/Module - Impulses on Stop and Land Setup", order = 4)]
    public class LAM_ImpulsesOnStopAndOnLand : LegsAnimatorControlModuleBase
    {
        public float MinimumImpulseCulldown = 0.1f;

        [FPD_Header("Triggering hips push impulses")]
        public LegsAnimator.PelvisImpulseSettings OnStopImpulse;
        public LegsAnimator.PelvisImpulseSettings OnLandImpulse;
        [FPD_Header("Set Zero Power to Not Use")]
        public LegsAnimator.PelvisImpulseSettings OnStartMoveImpulse;

        [Tooltip("Animator float parameter to control impulses power")]
        public string PowerMultiplierParameter = "";

        protected int _hash_powerMul = -1;

        readonly string powerMulStrN = "Power Multiplier";
        readonly string durMulStrN = "Duration Multiplier";
        readonly string spdLandPower = "Speed Affects Land";

        protected bool lastGrounded = true;
        protected bool lastMoving = false;

        protected float lastMovingTime = 0f;
        protected float lastUngroundedTime = 0f;
        protected float externalPowerMul = 1f;

        protected LegsAnimator.Variable _powerMulVar;
        protected LegsAnimator.Variable _durMulVar;
        protected LegsAnimator.Variable _spdAffectsLand;

        protected float customMul = 1f;
        protected float impulseCulldownElapsed = 0f;

        private void Reset()
        {
            OnStartMoveImpulse = new LegsAnimator.PelvisImpulseSettings();
            OnStartMoveImpulse.PowerMultiplier = 0f;
            OnStartMoveImpulse.LocalTranslation = new Vector3(0f, -0.05f, -0.001f);
        }

        public override void OnInit(LegsAnimator.LegsAnimatorCustomModuleHelper helper)
        {
            lastGrounded = true;
            lastMoving = false;

            _powerMulVar = helper.RequestVariable(powerMulStrN, 1f);
            _durMulVar = helper.RequestVariable(durMulStrN, 1f);
            _spdAffectsLand = helper.RequestVariable(spdLandPower, false);

            if (!string.IsNullOrWhiteSpace(PowerMultiplierParameter)) _hash_powerMul = Animator.StringToHash(PowerMultiplierParameter);
        }

        public override void OnUpdate(LegsAnimator.LegsAnimatorCustomModuleHelper helper)
        {
            var l = LA;

            impulseCulldownElapsed += l.DeltaTime;

            if (_hash_powerMul != -1) externalPowerMul = LA.Mecanim.GetFloat(_hash_powerMul);

            if (l.IsInAir)
            {
                if (l.InAirTime > 0f) lastUngroundedTime = l.InAirTime;
            }

            if (l.IsGrounded != lastGrounded)
            {
                if (OnLandImpulse.PowerMultiplier != 0f)
                    if (l.IsGrounded)
                    {
                        if (lastUngroundedTime > 0.1f)
                            Impact_OnLanding(l);
                    }
            }

            if (l.IsMoving)
            {
                if (l.MovingTime > 0f) lastMovingTime = l.MovingTime;
            }

            if (l.IsMoving != lastMoving)
            {
                if (OnStopImpulse.PowerMultiplier != 0f)
                    if (l.IsMoving == false)
                    {
                        if (lastMovingTime > 0.4f)
                            if (l.GroundedTime > 0.25f)
                                Impact_OnEndsMove(l);
                    }
                    else
                    {
                        if (OnStartMoveImpulse.PowerMultiplier != 0f)
                            if (l.GroundedTime > 0.1f)
                                Impact_OnStartMove(l);
                    }
            }

            lastGrounded = l.IsGrounded;
            lastMoving = l.IsMoving;
        }

        protected virtual void Impact_OnStartMove(LegsAnimator l)
        {
            if( impulseCulldownElapsed < MinimumImpulseCulldown ) return;
            impulseCulldownElapsed = 0f;
            l.User_AddImpulse( OnStartMoveImpulse, _powerMulVar.GetFloat() * customMul * externalPowerMul, _durMulVar.GetFloat());
        }

        protected virtual void Impact_OnEndsMove(LegsAnimator l)
        {
            if (impulseCulldownElapsed < MinimumImpulseCulldown) return;
            impulseCulldownElapsed = 0f;
            l.User_AddImpulse(OnStopImpulse, _powerMulVar.GetFloat() * customMul * externalPowerMul, _durMulVar.GetFloat());
        }

        protected virtual void Impact_OnLanding(LegsAnimator l)
        {
            if (impulseCulldownElapsed < MinimumImpulseCulldown) return;
            impulseCulldownElapsed = 0f;

            if (_spdAffectsLand.GetBool())
                l.User_AddImpulse(OnLandImpulse, _powerMulVar.GetFloat() * (customMul * externalPowerMul), _durMulVar.GetFloat());
            else
                l.User_AddImpulse(OnLandImpulse, _powerMulVar.GetFloat() * (customMul <= 0f ? 1f : customMul) * externalPowerMul, _durMulVar.GetFloat());
        }

        #region Editor Code
#if UNITY_EDITOR


        public override void Editor_InspectorGUI(LegsAnimator legsAnimator, LegsAnimator.LegsAnimatorCustomModuleHelper helper)
        {
            EditorGUILayout.HelpBox("Triggering hips push impulses when Legs Animator IsMoving and IsGrounded variables are changing.", UnityEditor.MessageType.Info);
            GUILayout.Space(4);

            var powerMulV = helper.RequestVariable(powerMulStrN, 1f);
            powerMulV.SetMinMaxSlider(0f, 3f);
            powerMulV.Editor_DisplayVariableGUI();

            var durMulV = helper.RequestVariable(durMulStrN, 1f);
            durMulV.SetMinMaxSlider(0f, 3f);
            durMulV.Editor_DisplayVariableGUI();

            var spdAffectLandPower = helper.RequestVariable(spdLandPower, false);
            spdAffectLandPower.AssignTooltip("Is movement speed affecting landing power? If true, then landing will not add implulse on jump in place.");
            spdAffectLandPower.Editor_DisplayVariableGUI();

            GUILayout.Space(4);
            if (GUILayout.Button("Go to module file for Push Impulses settings!"))
            {
                Selection.activeObject = helper.CurrentModule;
            }

            if (!legsAnimator.Mecanim)
            {
                UnityEditor.EditorGUILayout.HelpBox("No mecanim to detect parameters!", UnityEditor.MessageType.Warning);
                UnityEditor.EditorGUILayout.HelpBox("You can still use custom coding for legsAnimator.User_SetIsGrounded  and  legsAnimator.User_SetIsMoving", UnityEditor.MessageType.None);
                return;
            }

            bool wasWarning = false;
            if (string.IsNullOrWhiteSpace(legsAnimator.GroundedParameter))
            {
                wasWarning = true;
                UnityEditor.EditorGUILayout.HelpBox("No grounded parameter to detect landing! (Ignore this message if you set grounded state through code)", UnityEditor.MessageType.Warning);
            }

            if (!wasWarning)
                if (string.IsNullOrWhiteSpace(legsAnimator.MovingParameter))
                {
                    wasWarning = true;
                    UnityEditor.EditorGUILayout.HelpBox("No IsMoving parameter to detect stopping!  (Ignore this message if you set movement state through code)", UnityEditor.MessageType.Warning);
                }
        }


#endif
        #endregion


        #region Inspector Editor Class Ineritance
#if UNITY_EDITOR

        [UnityEditor.CanEditMultipleObjects]
        [UnityEditor.CustomEditor(typeof(LAM_ImpulsesOnStopAndOnLand))]
        public class LAM_ImpulsesOnStopAndOnLandEditor : LegsAnimatorControlModuleBaseEditor
        {
        }

#endif
        #endregion


    }
}