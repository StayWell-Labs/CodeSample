using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#if UNITY_ANDROID
using FantomLib;
#endif

#if UNITY_IOS
using KKSpeech;
#endif

namespace ChatbotSDK
{
    public class SpeakingManager : MonoBehaviour
    {
        public static SpeakingManager Instance;

        public CharacterAnimationHandler Character;

        public delegate void SpeechText(string text,float length);
        public event SpeechText OnUserSpeechText;

        public delegate void ChatSpeechText(ChatMessage message, float duration);
        public event ChatSpeechText OnChatSpeech;

        public delegate void SpeakingFailed();
        public event SpeakingFailed OnSpeakingFailed;

        public bool RecordAllWatsonAudio { get; set; } = false;

        public bool PushToTalkOff { get; set; } = false;

        public bool IsMuted { get; set; } = false;

        public bool IsNativeTextToSpeechSupported { get; set; } = false;

        public bool IsRecording
        {
            get
            {
                return TextToSpeechManager.Instance.IsRecording;
            }
        }

        public bool IsNotListening
        {
            get
            {
                if (IsChatSpeaking || PushToTalkOff || IsMuted ||
                    SpeechToTextManager.Instance.IgnoreListening)
                {
                    return true;
                }

                return false;
            }
        }

        private List<ChatMessage> chatMessages = new List<ChatMessage>();

        private bool aiEnabled;

        private const int MaxFailListenAttempts = 3;        
        private int failedListenAttempts = 0;

        public bool TextToSpeechActive
        {
            get
            {
                return TextToSpeechManager.Instance.IsActive;
            }
        }

        public bool SpeechToTextActive
        {
            get
            {
                return SpeechToTextManager.Instance.IsActive;
            }
        }

        public bool IgnoreListening
        {
            get
            {
                return SpeechToTextManager.Instance.IgnoreListening;
            }

            set
            {
                SpeechToTextManager.Instance.IgnoreListening = value;
            }
        }

        public bool UseNativeTTS
        {
            get
            {
                return SpeechToTextManager.Instance.UseNativeTTS;
            }
            set
            {
                SpeechToTextManager.Instance.UseNativeTTS = value;
            }
        }

        private void Awake()
        {
            if (Instance == null)
                Instance = this;
            else
                Destroy(gameObject);

#if UNITY_ANDROID && !UNITY_EDITOR
            if (AndroidPlugin.IsSupportedSpeechRecognizer())
            {
                IsNativeTextToSpeechSupported = true;
            }
#endif

#if UNITY_IOS && !UNITY_EDITOR

            if (SpeechRecognizer.ExistsOnDevice())
            {
                IsNativeTextToSpeechSupported = true;
            }

            if(OSXNativePlugin.getInstance().hasServices("") == 1)
            {
                 IsNativeTextToSpeechSupported = true;
            }
#endif
        }

        private void Start()
        {
            TextToSpeechManager.Instance.OnChatSpeech += SpeechPlaybackStarted;
        }

        private void OnDestroy()
        {
            if (TextToSpeechManager.Instance != null)
            {
                TextToSpeechManager.Instance.OnChatSpeech -= SpeechPlaybackStarted;
            }
        }

        public void StartSpeaking(string chatUrl, string chatEndpoint, string voice, bool cachedOnlyReplies, bool enableAI)
        {
            IBM.Cloud.SDK.LogSystem.InstallDefaultReactors();

            SpeechToTextManager.Instance.OnInput += SpeechToTextInput;

            SpeechToTextManager.Instance.Run();

            if (!string.IsNullOrEmpty(voice))
            {
                TextToSpeechManager.Instance.Voice = voice;
            }

            TextToSpeechManager.Instance.CachedOnly = cachedOnlyReplies;

            TextToSpeechManager.Instance.Run();

            if (enableAI)
            {
                if (!string.IsNullOrEmpty(chatUrl) && !string.IsNullOrEmpty(chatEndpoint))
                {
                    StayBluManager.Instance.ChatUrl = chatUrl;
                    StayBluManager.Instance.ChatEndpoint = chatEndpoint;
                }

                StayBluManager.Instance.OnOutput += ChatTextOutput;

                StayBluManager.Instance.Run();
            }

            aiEnabled = enableAI;
        }

        public bool IsChatSpeaking
        {
            get
            {
                if (TextToSpeechManager.Instance == null)
                {
                    return false;
                }

                return TextToSpeechManager.Instance.IsSpeaking;
            }
        }

        public bool DisableSpeaking()
        {
            SpeechToTextManager.Instance.OnInput -= SpeechToTextInput;

            SpeechToTextManager.Instance.Stop();
            TextToSpeechManager.Instance.Stop();

            if (aiEnabled)
            {
                StayBluManager.Instance.OnOutput -= ChatTextOutput;
                StayBluManager.Instance.Stop();
            }

            return true;
        }

        public void SpeechToTextInput(string text, float length)
        {
            OnUserSpeechText?.Invoke(text, length);
        }

        public void UserSays(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (aiEnabled)
            {
                StayBluManager.Instance.SendUserInput(text);
            }
        }

        public void ChatTextOutput(string text)
        {
            List<string> texts = new List<string>
            {
                text
            };

            ChatTextOutput(texts, "", "");
        }

        private void ChatTextOutput(List<string> texts, string tone, string timestamp)
        {
            ChatMessage.AddChatMessages(ref chatMessages, texts, tone, timestamp);
        }

        private void Update()
        {
            if (!SpeechToTextManager.Instance.UseNativeTTS)
            {
                if (!SpeechToTextManager.Instance.IsActive &&
                !SpeechToTextManager.Instance.IsInitializing)
                {
                    SpeechToTextManager.Instance.Run();

                    failedListenAttempts++;

                    if (failedListenAttempts >= MaxFailListenAttempts)
                    {
                        OnSpeakingFailed?.Invoke();
                    }
                }
                else if (SpeechToTextManager.Instance.IsActive)
                {
                    failedListenAttempts = 0;
                }
            }

            foreach (ChatMessage message in chatMessages)
            {
                if (!message.HasAudio)
                {
                    if (message.IsFailedAudio)
                    {
                        continue;
                    }

                    if (RecordedDialog.Instance.RecordedFileExists(message.Text))
                    {
                        message.HasAudio = true;
                    }
                    else if (TextToSpeechManager.Instance.CachedOnly && !message.IsEmoji)
                    {
                        message.Text = "Try that again. Maybe speak a little clearer.";
                        message.HasAudio = true;
                    }
                    else
                    {
                        if (!TextToSpeechManager.Instance.IsSynthesizing)
                        {
                            bool successStarting = TextToSpeechManager.Instance.GetAudio(message);
                            if (!successStarting && !message.IsEmoji)
                            {
                                message.AudioFails = ChatMessage.FailRetriesMax;
                                break;
                            }
                        }
                    }
                }
            }

            if (!TextToSpeechManager.Instance.IsSpeaking)
            {
                if (chatMessages.Count > 0)
                {
                    ChatMessage message = chatMessages[0];

                    if (message.IsEmoji)
                    {
                        if (Character != null)
                        {
                            Character.PlayAnimation(message.Text);
                        }

                        chatMessages.RemoveAt(0);

                    }
                    else if (message.HasAudio)
                    {
                        SpeakOutloud(message);

                        chatMessages.RemoveAt(0);
                    }
                    else if (message.IsFailedAudio)
                    {
                        OnSpeakingFailed?.Invoke();
                        OnChatSpeech?.Invoke(message, 3.0f);
                        chatMessages.RemoveAt(0);
                    }
                }
            }
        }

        public void RecordSpeech(string text, bool playAfterRecording)
        {
#if UNITY_IOS
            if (OSXNativePlugin.getInstance().isBackFromNative())
                OSXNativePlugin.getInstance().closeNativeAVAudioSession();
#endif
            TextToSpeechManager.Instance.RecordSpeech(text, playAfterRecording);
        }

        private void SpeakOutloud(ChatMessage message)
        {
            if (message.IsEmpty)
            {
                return;
            }
                
            if (RecordAllWatsonAudio)
            {
                RecordSpeech(message.Text, true);
                return;
            }

            float duration = RecordedDialog.Instance.PlayText(message);
            if (duration <= 0.0f)
            {
                TextToSpeechManager.Instance.PlayClip(message.Clip, message);
            }
        }

        public void SpeechPlaybackStarted(ChatMessage message, float duration)
        {
            OnChatSpeech?.Invoke(message, duration);
        }
    }
}