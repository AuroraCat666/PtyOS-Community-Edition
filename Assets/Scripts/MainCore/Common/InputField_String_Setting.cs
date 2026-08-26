using UnityEngine;
using UnityEngine.UI;

namespace MainCore.Common
{
    public class InputField_String_Setting : SettingBase<InputField, string>
    {
        protected override void OnStart()
        {
            SetValue(GetValue());
        }

        public override string GetValue()
        {
            return PlayerPrefs.GetString(dataTag, defaultValue);
        }

        public override void SetValue(string value)
        {
            if (DataContainer.isFocused) DataContainer.DeactivateInputField();
            DataContainer.text = value;
        }

        public override void SaveValue()
        {
            if (DataContainer.isFocused) DataContainer.DeactivateInputField();
            PlayerPrefs.SetString(dataTag, DataContainer.text);
        }
    }
}