using System;
using System.Collections.Generic;
using BoneLib.BoneMenu;
using MelonLoader;
using UnityEngine;

namespace GravityGloves
{
    // A value saved in MelonPreferences that also appears as an element on a BoneMenu page, or on several (like the
    // Quick settings page and its own page). Changing it on one page updates the others.
    internal abstract class Setting
    {
        protected Setting(string id, string name, string tooltip)
        {
            Id = id;
            Name = name;
            Tooltip = tooltip;
        }

        protected string Id { get; }
        protected string Name { get; }
        protected string Tooltip { get; }

        public abstract void CreateEntry(MelonPreferences_Category category);
        public abstract void AddTo(Page page);
        public abstract void Reset();

        // Shows the value on every page the setting is on. Setting an element's value may call its callback, which
        // would come back here, so it doesn't run again until it's done.
        private bool _showing;

        protected void Show<TElement>(List<TElement> elements, Action<TElement> show)
        {
            if (_showing)
                return;
            _showing = true;
            try
            {
                foreach (TElement element in elements)
                    show(element);
            }
            finally
            {
                _showing = false;
            }
        }
    }

    internal sealed class FloatSetting : Setting
    {
        private readonly float _default;
        private readonly float _min;
        private readonly float _max;
        private readonly float _step;
        private MelonPreferences_Entry<float> _entry;
        private readonly List<FloatElement> _elements = new List<FloatElement>();

        public FloatSetting(string id, string name, float defaultValue, float min, float max, float step, string tooltip)
            : base(id, name, tooltip)
        {
            _default = defaultValue;
            _min = min;
            _max = max;
            _step = step;
        }

        public float Value => Mathf.Clamp(_entry.Value, _min, _max);

        public override void CreateEntry(MelonPreferences_Category category) =>
            _entry = category.CreateEntry(Id, _default, Name, Tooltip);

        public override void AddTo(Page page)
        {
            FloatElement element = page.CreateFloat(Name, Color.white, Value, _step, _min, _max, value =>
            {
                // BoneMenu steps pile up float error (0.65000004), so round before saving.
                _entry.Value = (float)Math.Round(value, 3);
                Settings.Save();
                Show(_elements, e => e.Value = Value);
            });
            element.SetTooltip(Tooltip);
            _elements.Add(element);
        }

        public override void Reset()
        {
            _entry.Value = _default;
            Show(_elements, e => e.Value = _default);
        }
    }

    internal sealed class IntSetting : Setting
    {
        private readonly int _default;
        private readonly int _min;
        private readonly int _max;
        private readonly int _step;
        private MelonPreferences_Entry<int> _entry;
        private readonly List<IntElement> _elements = new List<IntElement>();

        public IntSetting(string id, string name, int defaultValue, int min, int max, int step, string tooltip)
            : base(id, name, tooltip)
        {
            _default = defaultValue;
            _min = min;
            _max = max;
            _step = step;
        }

        public int Value => Math.Clamp(_entry.Value, _min, _max);

        public override void CreateEntry(MelonPreferences_Category category) =>
            _entry = category.CreateEntry(Id, _default, Name, Tooltip);

        public override void AddTo(Page page)
        {
            IntElement element = page.CreateInt(Name, Color.white, Value, _step, _min, _max, value =>
            {
                _entry.Value = value;
                Settings.Save();
                Show(_elements, e => e.Value = Value);
            });
            element.SetTooltip(Tooltip);
            _elements.Add(element);
        }

        public override void Reset()
        {
            _entry.Value = _default;
            Show(_elements, e => e.Value = _default);
        }
    }

    internal sealed class BoolSetting : Setting
    {
        private readonly bool _default;
        private MelonPreferences_Entry<bool> _entry;
        private readonly List<BoolElement> _elements = new List<BoolElement>();

        public BoolSetting(string id, string name, bool defaultValue, string tooltip)
            : base(id, name, tooltip)
        {
            _default = defaultValue;
        }

        public bool Value => _entry.Value;

        public override void CreateEntry(MelonPreferences_Category category) =>
            _entry = category.CreateEntry(Id, _default, Name, Tooltip);

        public override void AddTo(Page page)
        {
            BoolElement element = page.CreateBool(Name, Color.white, Value, value =>
            {
                _entry.Value = value;
                Settings.Save();
                Show(_elements, e => e.Value = Value);
            });
            element.SetTooltip(Tooltip);
            _elements.Add(element);
        }

        public override void Reset()
        {
            _entry.Value = _default;
            Show(_elements, e => e.Value = _default);
        }
    }

    // Saved as the value's name, so the config file stays readable.
    internal sealed class EnumSetting<T> : Setting where T : struct, Enum
    {
        private readonly T _default;
        private MelonPreferences_Entry<string> _entry;
        private readonly List<EnumElement> _elements = new List<EnumElement>();

        public EnumSetting(string id, string name, T defaultValue, string tooltip)
            : base(id, name, tooltip)
        {
            _default = defaultValue;
        }

        public T Value { get; private set; }

        public override void CreateEntry(MelonPreferences_Category category)
        {
            _entry = category.CreateEntry(Id, _default.ToString(), Name, Tooltip);
            Value = Enum.TryParse(_entry.Value, out T value) ? value : _default;
        }

        public override void AddTo(Page page)
        {
            EnumElement element = page.CreateEnum(Name, Color.white, Value, value =>
            {
                Set((T)(object)value);
                Settings.Save();
                Show(_elements, e => e.Value = Value);
            });
            element.SetTooltip(Tooltip);
            _elements.Add(element);
        }

        public override void Reset()
        {
            Set(_default);
            Show(_elements, e => e.Value = _default);
        }

        private void Set(T value)
        {
            Value = value;
            _entry.Value = value.ToString();
        }
    }
}
