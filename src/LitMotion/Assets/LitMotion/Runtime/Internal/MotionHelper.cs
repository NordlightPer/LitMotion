using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Mathematics;

namespace LitMotion
{
    [BurstCompile]
    internal unsafe static class MotionHelper
    {
        [BurstCompile]
        public static void Update<TValue, TOptions, TAdapter>(MotionData<TValue, TOptions>* ptr, double time, out TValue result)
            where TValue : unmanaged
            where TOptions : unmanaged, IMotionOptions
            where TAdapter : unmanaged, IMotionAdapter<TValue, TOptions>
        {
            var corePtr = (MotionDataCore*)ptr;
            var prevStatus = corePtr->Status;

            // reset flag(s)
            corePtr->WasStatusChanged = false;

            corePtr->Time = time;
            time = math.max(time, 0.0);

            double t;
            bool isCompleted;
            bool isDelayed;
            int completedLoops;
            int clampedCompletedLoops;

            if (Hint.Unlikely(corePtr->Duration <= 0f))
            {
                if (corePtr->DelayType == DelayType.FirstLoop || corePtr->Delay == 0f)
                {
                    var timeSinceStart = time - corePtr->Delay;
                    isCompleted = corePtr->Loops >= 0 && timeSinceStart > 0f;
                    if (isCompleted)
                    {
                        t = 1f;
                        completedLoops = corePtr->Loops;
                    }
                    else
                    {
                        t = 0f;
                        completedLoops = timeSinceStart < 0f ? -1 : 0;
                    }
                    clampedCompletedLoops = GetClampedCompletedLoops(corePtr, completedLoops);
                    isDelayed = timeSinceStart < 0;
                }
                else
                {
                    completedLoops = (int)math.floor(time / corePtr->Delay);
                    clampedCompletedLoops = GetClampedCompletedLoops(corePtr, completedLoops);
                    isCompleted = corePtr->Loops >= 0 && clampedCompletedLoops > corePtr->Loops - 1;
                    isDelayed = !isCompleted;
                    t = isCompleted ? 1f : 0f;
                }
            }
            else
            {
                if (corePtr->DelayType == DelayType.FirstLoop)
                {
                    var timeSinceStart = time - corePtr->Delay;
                    completedLoops = (int)math.floor(timeSinceStart / corePtr->Duration);
                    clampedCompletedLoops = GetClampedCompletedLoops(corePtr, completedLoops);
                    isCompleted = corePtr->Loops >= 0 && clampedCompletedLoops > corePtr->Loops - 1;
                    isDelayed = timeSinceStart < 0f;

                    if (isCompleted)
                    {
                        t = 1f;
                    }
                    else
                    {
                        var currentLoopTime = timeSinceStart - corePtr->Duration * clampedCompletedLoops;
                        t = math.clamp(currentLoopTime / corePtr->Duration, 0f, 1f);
                    }
                }
                else
                {
                    var currentLoopTime = math.fmod(time, corePtr->Duration + corePtr->Delay) - corePtr->Delay;
                    completedLoops = (int)math.floor(time / (corePtr->Duration + corePtr->Delay));
                    clampedCompletedLoops = GetClampedCompletedLoops(corePtr, completedLoops);
                    isCompleted = corePtr->Loops >= 0 && clampedCompletedLoops > corePtr->Loops - 1;
                    isDelayed = currentLoopTime < 0;

                    if (isCompleted)
                    {
                        t = 1f;
                    }
                    else
                    {
                        t = math.clamp(currentLoopTime / corePtr->Duration, 0f, 1f);
                    }
                }
            }

            float progress;
            switch (corePtr->LoopType)
            {
                default:
                case LoopType.Restart:
                    progress = GetEasedValue(corePtr, (float)t);
                    break;
                case LoopType.Flip:
                    progress = GetEasedValue(corePtr, (float)t);
                    if ((clampedCompletedLoops + (int)t) % 2 == 1) progress = 1f - progress;
                    break;
                case LoopType.Incremental:
                    progress = GetEasedValue(corePtr, 1f) * clampedCompletedLoops + GetEasedValue(corePtr, (float)math.fmod(t, 1f));
                    break;
                case LoopType.Yoyo:
                    progress = (clampedCompletedLoops + (int)t) % 2 == 1
                        ? GetEasedValue(corePtr, (float)(1f - t))
                        : GetEasedValue(corePtr, (float)t);
                    break;
            }

            if (isCompleted)
            {
                corePtr->Status = MotionStatus.Completed;
            }
            else if (isDelayed || corePtr->Time < 0)
            {
                corePtr->Status = MotionStatus.Delayed;
            }
            else
            {
                corePtr->Status = MotionStatus.Playing;
            }

            corePtr->WasStatusChanged = prevStatus != corePtr->Status;

            var context = new MotionEvaluationContext()
            {
                Progress = progress
            };

            result = default(TAdapter).Evaluate(ref ptr->StartValue, ref ptr->EndValue, ref ptr->Options, context);
        }

        public static double GetTotalDuration(ref MotionDataCore dataRef)
        {
            if (dataRef.Loops < 0) return double.PositiveInfinity;
            return dataRef.Delay * (dataRef.DelayType == DelayType.EveryLoop ? dataRef.Loops : 1) +
                dataRef.Duration * dataRef.Loops;
        }

        [BurstCompile]
        public static double GetTotalDuration(MotionDataCore* dataPtr)
        {
            if (dataPtr->Loops < 0) return double.PositiveInfinity;
            return dataPtr->Delay * (dataPtr->DelayType == DelayType.EveryLoop ? dataPtr->Loops : 1) +
                dataPtr->Duration * dataPtr->Loops;
        }

        static int GetClampedCompletedLoops(MotionDataCore* corePtr, int completedLoops)
        {
            return corePtr->Loops < 0
                ? math.max(0, completedLoops)
                : math.clamp(completedLoops, 0, corePtr->Loops);
        }

        static float GetEasedValue(MotionDataCore* data, float value)
        {
            return data->Ease switch
            {
                Ease.CustomAnimationCurve => data->AnimationCurve.Evaluate(value),
                _ => EaseUtility.Evaluate(value, data->Ease)
            };
        }
    }
}