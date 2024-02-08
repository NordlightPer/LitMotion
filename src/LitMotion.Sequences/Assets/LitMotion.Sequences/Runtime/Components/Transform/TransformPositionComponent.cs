using UnityEngine;
using LitMotion.Extensions;

namespace LitMotion.Sequences.Components
{
    [SequenceComponentMenu("Transform/Position")]
    public sealed class TransformPositionComponent : SequenceComponentBase<Vector3, Transform>
    {
        [Header("Transform Settings")]
        public TransformScalingMode scalingMode;

        public override void ResetComponent()
        {
            base.ResetComponent();
            displayName = "Position";
            scalingMode = default;
        }

        public override void Configure(ISequencePropertyTable sequencePropertyTable, MotionSequenceItemBuilder builder)
        {
            var target = this.target.Resolve(sequencePropertyTable);
            if (target == null) return;
            
            if (!sequencePropertyTable.TryGetInitialValue<(Transform, TransformScalingMode), Vector3>((target, TransformScalingMode.Local), out var initialLocalPosition))
            {
                initialLocalPosition = target.localPosition;
                sequencePropertyTable.SetInitialValue((target, TransformScalingMode.Local), initialLocalPosition);
            }
            if (!sequencePropertyTable.TryGetInitialValue<(Transform, TransformScalingMode), Vector3>((target, TransformScalingMode.World), out var initialPosition))
            {
                initialPosition = target.position;
                sequencePropertyTable.SetInitialValue((target, TransformScalingMode.World), initialPosition);
            }

            var currentValue = Vector3.zero;

            switch (motionMode)
            {
                case MotionMode.Relative:
                    currentValue = scalingMode switch
                    {
                        TransformScalingMode.Local => initialLocalPosition,
                        TransformScalingMode.World => initialPosition,
                        _ => default
                    };
                    break;
                case MotionMode.Additive:
                    currentValue = scalingMode switch
                    {
                        TransformScalingMode.Local => target.localPosition,
                        TransformScalingMode.World => target.position,
                        _ => default
                    };
                    break;
            }

            var motionBuilder = LMotion.Create(currentValue + startValue, currentValue + endValue, duration);
            ConfigureMotionBuilder(ref motionBuilder);

            var handle = scalingMode switch
            {
                TransformScalingMode.Local => motionBuilder.BindToLocalPosition(target),
                TransformScalingMode.World => motionBuilder.BindToPosition(target),
                _ => default
            };

            builder.Add(handle);
        }

        public override void RestoreValues(ISequencePropertyTable sequencePropertyTable)
        {
            var target = this.target.Resolve(sequencePropertyTable);
            if (target == null) return;

            if (sequencePropertyTable.TryGetInitialValue<(Transform, TransformScalingMode), Vector3>((target, TransformScalingMode.Local), out var initialLocalPosition))
            {
                target.localPosition = initialLocalPosition;
            }
            if (sequencePropertyTable.TryGetInitialValue<(Transform, TransformScalingMode), Vector3>((target, TransformScalingMode.World), out var initialPosition))
            {
                target.position = initialPosition;
            }
        }
    }
}
