using System.Runtime.InteropServices;
using LitMotion.Collections;

namespace LitMotion
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MotionDataCore
    {
        // state
        public MotionStatus Status;
        public double Time;
        public float PlaybackSpeed;
        public bool IsPreserved;
        public bool WasStatusChanged;

        // parameters
        public float Duration;
        public Ease Ease;
#if LITMOTION_COLLECTIONS_2_0_OR_NEWER
        public NativeAnimationCurve AnimationCurve;
#else
        public UnsafeAnimationCurve AnimationCurve;
#endif
        public MotionTimeKind TimeKind;
        public float Delay;
        public int Loops;
        public DelayType DelayType;
        public LoopType LoopType;
    }
    
    /// <summary>
    /// A structure representing motion data.
    /// </summary>
    /// <typeparam name="TValue">The type of value to animate</typeparam>
    /// <typeparam name="TOptions">The type of special parameters given to the motion data</typeparam>
    [StructLayout(LayoutKind.Sequential)]
    public struct MotionData<TValue, TOptions>
        where TValue : unmanaged
        where TOptions : unmanaged, IMotionOptions
    {
        // Because of pointer casting, this field must always be placed at the beginning.
        public MotionDataCore Core;

        public TValue StartValue;
        public TValue EndValue;
        public TOptions Options;
    }
}