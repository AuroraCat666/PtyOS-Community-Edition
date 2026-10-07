using UnityEngine;
using UnityEngine.UI;

namespace MainCore.Common
{
    [RequireComponent(typeof(Text))]
    public class VersionText : MonoBehaviour
    {
        private void Awake()
        {
            Text text = GetComponent<Text>();
            text.text = $"PtyOS(Community Edition.) V0.7 by kagari939 & Yuncishu\n";
        }
        // Start is called before the first frame update
        void Start()
        {
        
        }

        // Update is called once per frame
        void Update()
        {
        
        }
    }
}
