using System;
using System.Runtime.CompilerServices;
using LitMotion.Collections;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace LitMotion
{
    internal interface IMotionStorage
    {
        bool IsActive(MotionHandle handle);
        bool TryCancel(MotionHandle handle);
        bool TryComplete(MotionHandle handle);
        void Cancel(MotionHandle handle);
        void Complete(MotionHandle handle);
        void SetTime(MotionHandle handle, double time);
        ref MotionDataCore GetDataRef(MotionHandle handle);
        ref ManagedMotionData GetManagedDataRef(MotionHandle handle);
        void Reset();
    }


    internal sealed class MotionStorage<TValue, TOptions, TAdapter> : IMotionStorage
        where TValue : unmanaged
        where TOptions : unmanaged, IMotionOptions
        where TAdapter : unmanaged, IMotionAdapter<TValue, TOptions>
    {
        const int InitialCapacity = 32;

        public int Id { get; }
        public int Count => tail;

        SparseSetCore sparseSetCore = new(InitialCapacity);
        SparseIndex[] sparseIndexLookup = new SparseIndex[InitialCapacity];
        MotionData<TValue, TOptions>[] unmanagedDataArray = new MotionData<TValue, TOptions>[InitialCapacity];
        ManagedMotionData[] managedDataArray = new ManagedMotionData[InitialCapacity];
        AllocatorHelper<RewindableAllocator> allocator;
        int tail;

        public MotionStorage(int id)
        {
            Id = id;
            allocator = RewindableAllocatorFactory.CreateAllocator();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<MotionData<TValue, TOptions>> GetDataSpan()
        {
            return unmanagedDataArray.AsSpan();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<ManagedMotionData> GetManagedDataSpan()
        {
            return managedDataArray.AsSpan();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity(int minimumCapacity)
        {
            sparseSetCore.EnsureCapacity(minimumCapacity);
            ArrayHelper.EnsureCapacity(ref sparseIndexLookup, minimumCapacity);
            ArrayHelper.EnsureCapacity(ref unmanagedDataArray, minimumCapacity);
            ArrayHelper.EnsureCapacity(ref managedDataArray, minimumCapacity);
        }

        public unsafe MotionHandle Create(ref MotionBuilder<TValue, TOptions, TAdapter> builder)
        {
            EnsureCapacity(tail + 1);
            var buffer = builder.buffer;

            ref var dataRef = ref unmanagedDataArray[tail];
            ref var managedDataRef = ref managedDataArray[tail];

            dataRef.Core.Status = MotionStatus.Scheduled;
            dataRef.Core.Time = 0;
            dataRef.Core.PlaybackSpeed = 1f;
            dataRef.Core.IsPreserved = false;
            dataRef.Core.TimeKind = buffer.TimeKind;

            dataRef.Core.Duration = buffer.Duration;
            dataRef.Core.Delay = buffer.Delay;
            dataRef.Core.DelayType = buffer.DelayType;
            dataRef.Core.Ease = buffer.Ease;
            dataRef.Core.Loops = buffer.Loops;
            dataRef.Core.LoopType = buffer.LoopType;
            dataRef.StartValue = buffer.StartValue;
            dataRef.EndValue = buffer.EndValue;
            dataRef.Options = buffer.Options;

            if (buffer.Ease == Ease.CustomAnimationCurve)
            {
                if (dataRef.Core.AnimationCurve.IsCreated)
                {
                    dataRef.Core.AnimationCurve.CopyFrom(buffer.AnimationCurve);
                }
                else
                {
#if LITMOTION_COLLECTIONS_2_0_OR_NEWER
                    dataRef.Core.AnimationCurve = new NativeAnimationCurve(buffer.AnimationCurve, allocator.Allocator.Handle);
#else
                    dataRef.Core.AnimationCurve = new UnsafeAnimationCurve(buffer.AnimationCurve, allocator.Allocator.Handle);
#endif
                }
            }

            managedDataRef.CancelOnError = buffer.CancelOnError;
            managedDataRef.SkipValuesDuringDelay = buffer.SkipValuesDuringDelay;
            managedDataRef.UpdateAction = buffer.UpdateAction;
            managedDataRef.OnCancelAction = buffer.OnCancelAction;
            managedDataRef.OnCompleteAction = buffer.OnCompleteAction;
            managedDataRef.StateCount = buffer.StateCount;
            managedDataRef.State0 = buffer.State0;
            managedDataRef.State1 = buffer.State1;
            managedDataRef.State2 = buffer.State2;

            if (buffer.BindOnSchedule && buffer.UpdateAction != null)
            {
                managedDataRef.UpdateUnsafe(
                    default(TAdapter).Evaluate(
                        ref dataRef.StartValue,
                        ref dataRef.EndValue,
                        ref dataRef.Options,
                        new()
                        {
                            Progress = dataRef.Core.Ease switch
                            {
                                Ease.CustomAnimationCurve => buffer.AnimationCurve.Evaluate(0f),
                                _ => EaseUtility.Evaluate(0f, dataRef.Core.Ease)
                            }
                        }
                ));
            }

            var sparseIndex = sparseSetCore.Alloc(tail);
            sparseIndexLookup[tail] = sparseIndex;

            tail++;

            return new MotionHandle()
            {
                Index = sparseIndex.Index,
                Version = sparseIndex.Version,
                StorageId = Id
            };
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void RemoveAt(int denseIndex)
        {
            tail--;

            // swap elements
            unmanagedDataArray[denseIndex] = unmanagedDataArray[tail];
            unmanagedDataArray[tail] = default;
            managedDataArray[denseIndex] = managedDataArray[tail];
            managedDataArray[tail] = default;

            // swap sparse index
            var prevSparseIndex = sparseIndexLookup[denseIndex];
            var currentSparseIndex = sparseIndexLookup[denseIndex] = sparseIndexLookup[tail];
            sparseIndexLookup[tail] = default;

            // update slot
            if (currentSparseIndex.Version != 0)
            {
                ref var slot = ref sparseSetCore.GetSlotRefUnchecked(currentSparseIndex.Index);
                slot.DenseIndex = denseIndex;
            }

            // free slot
            if (prevSparseIndex.Version != 0)
            {
                sparseSetCore.Free(prevSparseIndex);
            }
        }

        public void RemoveAll(NativeList<int> denseIndexList)
        {
            var list = new NativeArray<SparseIndex>(denseIndexList.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < list.Length; i++)
            {
                list[i] = sparseIndexLookup[denseIndexList[i]];
            }

            for (int i = 0; i < list.Length; i++)
            {
                RemoveAt(sparseSetCore.GetSlotRefUnchecked(list[i].Index).DenseIndex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsActive(MotionHandle handle)
        {
            ref var slot = ref sparseSetCore.GetSlotRefUnchecked(handle.Index);
            if (IsDenseIndexOutOfRange(slot.DenseIndex)) return false;
            if (IsInvalidVersion(slot.Version, handle)) return false;

            ref var motion = ref unmanagedDataArray[slot.DenseIndex];
            return motion.Core.Status is MotionStatus.Scheduled or MotionStatus.Delayed or MotionStatus.Playing;
        }

        public bool TryCancel(MotionHandle handle)
        {
            return TryCancelCore(handle) == 0;
        }

        public void Cancel(MotionHandle handle)
        {
            switch (TryCancelCore(handle))
            {
                case 1:
                    Error.MotionNotExists();
                    return;
                case 2:
                    Error.MotionHasBeenCanceledOrCompleted();
                    return;
            }
        }

        int TryCancelCore(MotionHandle handle)
        {
            ref var slot = ref sparseSetCore.GetSlotRefUnchecked(handle.Index);
            var denseIndex = slot.DenseIndex;
            if (IsDenseIndexOutOfRange(denseIndex))
            {
                return 1;
            }

            ref var unmanagedData = ref unmanagedDataArray[denseIndex];
            if (IsInvalidVersion(slot.Version, handle))
            {
                return 1;
            }

            if (unmanagedData.Core.Status is MotionStatus.None or MotionStatus.Canceled or MotionStatus.Completed)
            {
                return 2;
            }

            unmanagedData.Core.Status = MotionStatus.Canceled;

            ref var managedData = ref managedDataArray[denseIndex];
            try
            {
                managedData.OnCancelAction?.Invoke();
            }
            catch (Exception ex)
            {
                MotionDispatcher.GetUnhandledExceptionHandler()?.Invoke(ex);
            }

            return 0;
        }

        public bool TryComplete(MotionHandle handle)
        {
            return TryCompleteCore(handle) == 0;
        }

        public void Complete(MotionHandle handle)
        {
            switch (TryCompleteCore(handle))
            {
                case 1:
                    Error.MotionNotExists();
                    return;
                case 2:
                    Error.MotionHasBeenCanceledOrCompleted();
                    return;
                case 3:
                    throw new InvalidOperationException("Complete was ignored because it is not possible to complete a motion that loops infinitely. If you want to end the motion, call Cancel() instead.");
            }
        }

        int TryCompleteCore(MotionHandle handle)
        {
            ref var slot = ref sparseSetCore.GetSlotRefUnchecked(handle.Index);

            if (IsDenseIndexOutOfRange(slot.DenseIndex))
            {
                return 1;
            }

            ref var unmanagedData = ref unmanagedDataArray[slot.DenseIndex];

            var version = slot.Version;
            if (IsInvalidVersion(version, handle))
            {
                return 1;
            }

            if (unmanagedData.Core.Status is MotionStatus.None or MotionStatus.Canceled or MotionStatus.Completed)
            {
                return 2;
            }

            if (unmanagedData.Core.Loops < 0)
            {
                return 3;
            }

            ref var managedData = ref managedDataArray[slot.DenseIndex];

            unmanagedData.Core.Status = MotionStatus.Completed;

            var endProgress = unmanagedData.Core.LoopType switch
            {
                LoopType.Restart => 1f,
                LoopType.Flip or LoopType.Yoyo => unmanagedData.Core.Loops % 2 == 0 ? 0f : 1f,
                LoopType.Incremental => unmanagedData.Core.Loops,
                _ => 1f
            };

            var easedEndProgress = unmanagedData.Core.Ease switch
            {
                Ease.CustomAnimationCurve => unmanagedData.Core.AnimationCurve.Evaluate(endProgress),
                _ => EaseUtility.Evaluate(endProgress, unmanagedData.Core.Ease),
            };

            try
            {
                var endValue = default(TAdapter).Evaluate(
                    ref unmanagedData.StartValue,
                    ref unmanagedData.EndValue,
                    ref unmanagedData.Options,
                    new() { Progress = easedEndProgress }
                );

                managedData.UpdateUnsafe(endValue);
            }
            catch (Exception ex)
            {
                MotionDispatcher.GetUnhandledExceptionHandler()?.Invoke(ex);
            }

            try
            {
                managedData.OnCompleteAction?.Invoke();
            }
            catch (Exception ex)
            {
                MotionDispatcher.GetUnhandledExceptionHandler()?.Invoke(ex);
            }

            return 0;
        }

        public unsafe void SetTime(MotionHandle handle, double time)
        {
            ref var slot = ref sparseSetCore.GetSlotRefUnchecked(handle.Index);

            var denseIndex = slot.DenseIndex;
            if (IsDenseIndexOutOfRange(denseIndex)) Error.MotionNotExists();

            fixed (MotionData<TValue, TOptions>* ptr = unmanagedDataArray)
            {
                var dataPtr = ptr + denseIndex;

                var version = slot.Version;
                if (version <= 0 || version != handle.Version) Error.MotionNotExists();

                MotionHelper.Update<TValue, TOptions, TAdapter>(dataPtr, time, out var result);

                var status = dataPtr->Core.Status;
                ref var managedData = ref managedDataArray[denseIndex];

                if (status == MotionStatus.Playing || (status == MotionStatus.Delayed && !managedData.SkipValuesDuringDelay))
                {
                    try
                    {
                        managedData.UpdateUnsafe(result);
                    }
                    catch (Exception ex)
                    {
                        MotionDispatcher.GetUnhandledExceptionHandler()?.Invoke(ex);
                        if (managedData.CancelOnError)
                        {
                            dataPtr->Core.Status = MotionStatus.Canceled;
                            managedData.OnCancelAction?.Invoke();
                        }
                    }
                }
                else if (status == MotionStatus.Completed)
                {
                    try
                    {
                        managedData.UpdateUnsafe(result);
                    }
                    catch (Exception ex)
                    {
                        MotionDispatcher.GetUnhandledExceptionHandler()?.Invoke(ex);
                        if (managedData.CancelOnError)
                        {
                            dataPtr->Core.Status = MotionStatus.Canceled;
                            managedData.OnCancelAction?.Invoke();
                            return;
                        }
                    }

                    if (dataPtr->Core.WasStatusChanged)
                    {
                        try
                        {
                            managedData.OnCompleteAction?.Invoke();
                        }
                        catch (Exception ex)
                        {
                            MotionDispatcher.GetUnhandledExceptionHandler()?.Invoke(ex);
                        }
                    }
                }
            }
        }

        public ref ManagedMotionData GetManagedDataRef(MotionHandle handle)
        {
            ref var slot = ref GetSlotWithVarify(handle);
            return ref managedDataArray[slot.DenseIndex];
        }

        public ref MotionDataCore GetDataRef(MotionHandle handle)
        {
            ref var slot = ref GetSlotWithVarify(handle);
            return ref UnsafeUtility.As<MotionData<TValue, TOptions>, MotionDataCore>(ref unmanagedDataArray[slot.DenseIndex]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ref SparseSetCore.Slot GetSlotWithVarify(MotionHandle handle)
        {
            ref var slot = ref sparseSetCore.GetSlotRefUnchecked(handle.Index);
            if (IsDenseIndexOutOfRange(slot.DenseIndex)) Error.MotionNotExists();

            if (IsInvalidVersion(slot.Version, handle) ||
                unmanagedDataArray[slot.DenseIndex].Core.Status == MotionStatus.None)
            {
                Error.MotionNotExists();
            }

            return ref slot;
        }

        public void Reset()
        {
            sparseSetCore.Reset();
            sparseIndexLookup.AsSpan().Clear();
            unmanagedDataArray.AsSpan().Clear();
            managedDataArray.AsSpan().Clear();
            tail = 0;
            allocator.Allocator.Rewind();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsDenseIndexOutOfRange(int denseIndex)
        {
            return denseIndex < 0 || denseIndex >= tail;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsInvalidVersion(int version, MotionHandle handle)
        {
            return version <= 0 || version != handle.Version;
        }
    }
}