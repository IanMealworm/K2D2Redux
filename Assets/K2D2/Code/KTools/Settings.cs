namespace KTools
{
    public interface IResettable
    {
        void Reset(string path);
    }

    public class EnumSetting <TEnum> : IResettable where TEnum : struct
    {
        public string path;
        TEnum default_value;
        // Which physical settings file this setting reads/writes - defaults to the mod's single
        // shared file (SettingsFile.Instance) so every existing call site that doesn't pass this
        // is completely unaffected. Only settings that need to live in their own separate file
        // (see LandingSettings.cs's Atmo/Vacuum profile split) pass a different one.
        readonly SettingsFile file;
        public EnumSetting(string path, TEnum default_value, SettingsFile file = null)
        {
            this.path = path;
            this.default_value = default_value;
            this.file = file ?? SettingsFile.Instance;
            if (this.file.loaded)
                loadValue();
            else
                this.file.onloaded_event += loadValue;

            if (!string.IsNullOrEmpty(path))
                this.file.reset_register.Add(this);
        }

        public void Reset(string path = null)
        {
            if (path != null)
                if (!this.path.StartsWith(path))
                    return;

            this.V = default_value;
        }

        void loadValue()
        {
            _value = file.GetEnum<TEnum>(path, default_value);
        }

        TEnum _value;

        public TEnum V
        {
            get { return _value; }
            set
            {
                if (value.Equals(_value)) return;

                _value = value;
                listeners?.Invoke(this.V);

                file.SetEnum<TEnum>(path, _value);
            }
        }

        public int int_value
        {
            get { return (int)(object) V;}
            set { _value = (TEnum)(object) value;}
        }

        public delegate void onChanged(TEnum value);

        public event onChanged listeners;

        // add listener and call it once
        public void listen(onChanged listener)
        {
            
            listeners+= listener;
            listener(V);
        }
    }

    public class Setting<T> : IResettable
    {
        public string key;
        T default_value;
        // Same idea as EnumSetting's own `file` field above - defaults to the shared singleton so
        // every existing caller is unaffected; only settings needing a separate physical file
        // (see LandingSettings.cs) pass a different one.
        protected readonly SettingsFile file;

        public Setting(string path, T default_value, SettingsFile file = null)
        {
            this.key = path;
            this.default_value = default_value;
            this.file = file ?? SettingsFile.Instance;
            if (this.file.loaded)
                loadValue();
            else
                this.file.onloaded_event += loadValue;

            if (!string.IsNullOrEmpty(path))
                this.file.reset_register.Add(this);
        }

        public void Reset(string path = null)
        {
            if (path != null)
                if (!this.key.StartsWith(path))
                    return;

            this.V = default_value;
        }

        void loadValue()
        {
            _value = file.Get<T>(key, default_value);
            // if (path == "lift.end_ascent_pc")
            //     Debug.Log("load value" + _value);
        }

        T _value;
        public virtual T V
        {
            get { return _value; }
            set
            {
                if (value.Equals(_value))
                    return;

                if (!string.IsNullOrEmpty(key))
                    if (!file.Set<T>(key, value))
                        return;

                _value = value;
                // if (path == "lift.end_ascent_pc")
                //     Debug.Log("set value" + _value);
                listeners?.Invoke(this.V);
            }
        }

        public delegate void onChanged(T value);

        public event onChanged listeners;

        // add listener and call it once
        public void listen(onChanged listener)
        {
            listeners+= listener;
            listener(V);
        }

        protected void NotifyListeners() => listeners?.Invoke(V);
    }


    // A setting kept within [min, max]: values set in code or read back from the settings file
    // are clamped, not just the slider bound to it.
    public class ClampSetting<T> : Setting<T> where T : System.IComparable<T>
    {
        T _min;
        public T min
        {
            get { return _min; }
            set
            {
                _min = value;
                V = base.V;
            }
        }

        T _max;
        public T max
        {
            get { return _max; }
            set
            {
                _max = value;
                V = base.V;
            }
        }

        public ClampSetting(string path, T default_value, T min, T max, SettingsFile file = null): base(path, default_value, file)
        {
            _min = min;
            _max = max;
        }

        public override T V
        {
            // The file can load after construction, so its value is clamped on the way out too.
            get => Extensions.Clamp(base.V, _min, _max);
            set
            {
                T clamped = Extensions.Clamp(value, _min, _max);
                base.V = clamped;
                // A field showing the out-of-range value is put back to the clamped one, even
                // when the stored value didn't change.
                if (!clamped.Equals(value))
                    NotifyListeners();
            }
        }
    }
}