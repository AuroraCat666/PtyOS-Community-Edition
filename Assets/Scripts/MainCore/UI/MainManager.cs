using System;
using System.Text;
using DG.Tweening;
using MainCore.Common;
using MainCore.Data;
using MainCore.UI.Utils;
using MainCore.Utilities;
using Newtonsoft.Json;
using SFB;
using UnityEngine;
using UnityEngine.UI;

namespace MainCore.UI
{
    public class MainManager : MonoBehaviour
    {
        [SerializeField] private Button settings;
        [SerializeField] private Button singlePlay, multiPlay, login;
        [SerializeField] private GameObject characterSelectionObj;
        [SerializeField] private Button openCharaPreview, closeCharaPreview;
        [SerializeField] private DatuPreviewFadeInOut datuPreviewFadeInOut;
        [SerializeField] private Button openCharacterSelections,
            deleteCharacter,
            selectCharacter,
            editCharacter,
            closeCharacterSelections;

        [SerializeField] private SpriteRenderer character, datuCharacter;
        [SerializeField] private Sprite defaultCharacter;

        private static CharacterImage characterImage;
        public static MainManager Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            settings.onClick.AddListener(() =>
            {
                PlayUiSound();
                SetBgmVolume(0.5f);
                SceneTransit.Instance.LoadAdditiveScene("SettingsScene");
            });
            singlePlay.onClick.AddListener(() =>
            {
                PlayUiSound();
                if (GlobalSetting.IsOffline)
                {
                    InGameUIManager.ShowModalWindowWithClose("提示", "此功能需登录后才可使用", () => { }, "确定");
                    return;
                }
                SceneTransit.Instance.LoadScene("BeatmapSelectScene", 0);
            });
            multiPlay.onClick.AddListener(() =>
            {
                PlayUiSound();
                SceneTransit.Instance.LoadScene("NetworkTest");
            });
            login.onClick.AddListener(() => SceneTransit.Instance.LoadScene("LoginScene"));
            openCharaPreview.onClick.AddListener(() =>
            {
                datuPreviewFadeInOut.FadeIn(0.15f, 0.05f);
                datuCharacter.sprite = character.sprite;
            });
            closeCharaPreview.onClick.AddListener(() => datuPreviewFadeInOut.FadeOut(0.15f, 0.05f));
            openCharacterSelections.onClick.AddListener(() =>
            {
                PlayUiSound();
                OpenCharacterOptions();
            });
#if false
            openCharacterSelections.onClick.AddListener(OpenCharacterSelector);
#endif
            deleteCharacter.onClick.AddListener(() =>
            {
                PlayUiSound();
                characterImage = null;
                character.sprite = defaultCharacter;
                PlayerPrefs.DeleteKey("character");
                PlayerPrefs.Save();
            });
            deleteCharacter.onClick.AddListener(CloseCharacterOptions);
            selectCharacter.onClick.AddListener(() =>
            {
                PlayUiSound();
                ImportCharacterPackage();
            });
            selectCharacter.onClick.AddListener(CloseCharacterOptions);
            closeCharacterSelections.onClick.AddListener(() =>
            {
                PlayUiSound();
                CloseCharacterOptions();
            });
#if UNITY_EDITOR
            editCharacter.onClick.AddListener(() =>
            {
                PlayUiSound();
                SceneTransit.Instance.LoadScene("CharacterAdjustScene");
            });
            editCharacter.interactable = true;
            editCharacter.transform.Find("Mask").Find("Icon").gameObject.GetComponent<Image>().SetAlpha(1f);
#else
            editCharacter.interactable = false;
            editCharacter.transform.Find("Mask").Find("Icon").gameObject.GetComponent<Image>().SetAlpha(0.5f);
#endif
            multiPlay.gameObject.SetActive(!GlobalSetting.IsOffline);
            login.gameObject.SetActive(GlobalSetting.IsOffline);
        }

        private AudioSource fuck;
        private AudioClip uiClickSound;
        private AudioSource bgmSource;
        private AudioClip bgmClip;

        private void PlayUiSound()
        {
            if (uiClickSound == null)
            {
                uiClickSound = Resources.Load<AudioClip>("Audio/dragon-studio-button-press-382713");
            }
            if (uiClickSound != null)
            {
                AudioSource.PlayClipAtPoint(uiClickSound, Camera.main != null ? Camera.main.transform.position : Vector3.zero, 1f);
            }
        }

        public void Start()
        {
            var currentRes = GlobalSetting.OriginResolution;
            if (PlayerPrefsExtension.GetBoolean("half_res", false))
            {
                Debug.Log("[SettingManager] Half Resolution Mode Enabled");
                Screen.SetResolution(currentRes.width /= 2, currentRes.height /= 2, Screen.fullScreenMode);
            }
            else
            {
                Debug.Log("[SettingManager] Half Resolution Mode Disabled");
                Screen.SetResolution(currentRes.width, currentRes.height, Screen.fullScreenMode);
            }

            Application.targetFrameRate = PlayerPrefs.GetInt("refresh_rate", 60);

            if (characterImage == null && PlayerPrefs.HasKey("character"))
            {
                try
                {
                    string[] strings = PlayerPrefs.GetString("character").Split("\n");
                    if (strings.Length == 3)
                    {
                        byte[] infoData = Encoding.UTF8.GetBytes(strings[0]);
                        byte[] imageData = Convert.FromBase64String(strings[1]);
                        byte[] hashData = Convert.FromBase64String(strings[2]);

                        if (Util.ValidateFileHash(hashData, imageData, infoData))
                        {
                            characterImage = new CharacterImage
                            {
                                TextureData = imageData,
                                InfoData = infoData,
                                HashData = hashData,
                                Info = JsonConvert.DeserializeObject<ExternalCharacterInfo>(strings[0])
                            };
                            if (characterImage.Info == null) throw new NullReferenceException();
                        }
                        else
                        {
                            PlayerPrefs.DeleteKey("character");
                            PlayerPrefs.Save();
                        }
                    }
                    else
                    {
                        PlayerPrefs.DeleteKey("character");
                        PlayerPrefs.Save();
                    }
                }
                catch (Exception ex)
                {
#if UNITY_EDITOR
                    Debug.LogError("[MainManager] 无法读取自定义角色");
                    Debug.LogException(ex);
#endif
                    PlayerPrefs.DeleteKey("character");
                    PlayerPrefs.Save();
                }
            }
            character.sprite = characterImage == null ? character.sprite = defaultCharacter : characterImage.Sprite;

            CloseCharacterOptions();
            datuPreviewFadeInOut.FadeOut(0f);
            Update();

            PlayBgm();
        }

        private void OnEnable()
        {
            SceneTransit.OnSceneClosing.AddListener(FadeOutBgm);
        }

        private void OnDisable()
        {
            SceneTransit.OnSceneClosing.RemoveListener(FadeOutBgm);
        }

        private void PlayBgm()
        {
            if (bgmClip == null)
            {
                bgmClip = Resources.Load<AudioClip>("Audio/PhigrOS Ending（Phigros 四周年版）");
            }
            if (bgmClip == null) return;
            if (bgmSource == null) bgmSource = gameObject.AddComponent<AudioSource>();
            bgmSource.clip = bgmClip;
            bgmSource.loop = true;
            bgmSource.playOnAwake = false;
            bgmSource.volume = 0f;
            bgmSource.Play();
            bgmSource.DOFade(1f, 1.2f).SetEase(Ease.InQuad);
        }

        private void FadeOutBgm()
        {
            if (bgmSource == null || !bgmSource.isPlaying) return;
            bgmSource.DOKill();
            bgmSource.DOFade(0f, 0.8f).SetEase(Ease.OutQuad).OnComplete(() => bgmSource.Stop());
        }

        public void SetBgmVolume(float v)
        {
            if (bgmSource == null) return;
            bgmSource.DOKill();
            bgmSource.DOFade(v, 0.3f);
        }

        private void Update()
        {
#if UNITY_EDITOR || UNITY_STANDALONE
            if (!Input.GetKeyDown(KeyCode.Escape)) return;
            if (!InGameUIManager.IsActive) InGameUIManager.ShowModalWindowWithClose("提示", "确定要退出吗？", Util.QuitApp, "是", () => {},  "否");
#endif
        }

        private void CloseCharacterOptions()
        {
            characterSelectionObj.SetActive(false);
        }

        private void OpenCharacterOptions()
        {
            deleteCharacter.interactable = characterImage != null;
            characterSelectionObj.SetActive(true);
        }

        private void OpenCharacterSelector()
        {
        }

        private void ImportCharacterPackage()
        {
            OpenFile.LoadFile(path =>
            {
                Sprite sprite = OnSelectedCharacterPackage(path);
                if (sprite) character.sprite = sprite;
            }, () => { }, new []{new ExtensionFilter("REP角色包", "charapkg")}, null, "选择立绘包...", "确定");
        }

        private Sprite OnSelectedCharacterPackage(string path)
        {
            try
            {
                characterImage = Util.GetCharacterImage(path,
                    () => InGameUIManager.ShowModalWindowWithClose("错误", "文件不合法", () => { }, "确定"),
                    () => InGameUIManager.ShowModalWindowWithClose("错误", "文件格式不正确", () => { }, "确定"));
                Sprite readSprite = characterImage.Sprite;
                PlayerPrefs.SetString("character", characterImage.ToString());
                PlayerPrefs.Save();
                return readSprite;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (FormatException)
            {
                InGameUIManager.ShowModalWindowWithClose("错误", "文件格式不正确", () => { }, "确定");
                return null;
            }
        }
    }

    public class CharacterImage
    {
        public byte[] InfoData;
        public byte[] TextureData;
        public byte[] HashData;
        public ExternalCharacterInfo Info;
        public Texture2D Texture => Util.ReadFileAsTexture(TextureData);
        public Sprite Sprite => Util.ReadSprite(Texture, Info.Pivot, Info.PixelsPerUnit);

        public override string ToString()
        {
            return Encoding.UTF8.GetString(InfoData) + "\n" + Convert.ToBase64String(TextureData) + "\n" + Convert.ToBase64String(HashData);
        }
    }
}