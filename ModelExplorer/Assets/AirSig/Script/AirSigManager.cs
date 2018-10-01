using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.XR.WSA.Input;

#if WINDOWS_UWP
using System.IO;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;
using Windows.Storage;
#elif UNITY_EDITOR
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
#endif

namespace AirSig {
    public class AirSigManager : MonoBehaviour {

        /// Enable debug logging
        public bool EnableDebugLog = true;

        // Default interval for sensor sampling rate.
        // Increasing this makes less sample for a fixed period of time
        public float MinSampleInterval = 12f;

        /// Threshold score for a common gesture to be considered pass
        public const int COMMON_MISTOUCH_THRESHOLD = 30; //0.5sec (1 sec is about 60)
        public const float COMMON_PASS_THRESHOLD = 0.9f;
        public const float SMART_TRAIN_PASS_THRESHOLD = 0.8f;

        /// Threshold for engine
        public const float THRESHOLD_TRAINING_MATCH_THRESHOLD = 0.98f;
        public const float THRESHOLD_VERIFY_MATCH_THRESHOLD = 0.98f;
        public const float THRESHOLD_IS_TWO_GESTURE_SIMILAR = 1.0f;

        /// Identification/Training mode
        public enum Mode : int {
            None = 0x00, // will not perform any identification`
            IdentifyPlayerSignature = 0x02, // will perform user defined gesture identification
            DeveloperDefined = 0x08, // will perform predefined common identification
            TrainPlayerSignature = 0x10, // will perform training of a specific target
            AddPlayerGesture = 0x40,
            IdentifyPlayerGesture = 0x80,
            SmartTrainDeveloperDefined = 0x100,
            SmartIdentifyDeveloperDefined = 0x200
        };

        /// Errors used in OnPlayerSignatureTrained callback
        public class Error {

            public static readonly int SIGN_TOO_FEW_WORD = -204;
            public static readonly int SIGN_WITH_MISTOUCH = -200;

            public int code;
            public String message;

            public Error(int errorCode, String message) {
                this.code = errorCode;
                this.message = message;
            }
        }

        /// Strength used in OnPlayerSignatureTrained callback
        public enum SecurityLevel : int {
            None = 0,
            Very_Poor = 1,
            Poor = 2,
            Normal = 3,
            High = 4,
            Very_High = 5
        }

        // Mode of operation
        private Mode mCurrentMode = Mode.None;

        // Current target for 
        private List<int> mCurrentTarget = new List<int>();
        private List<string> mCurrentPredefined = new List<string>();
        private string mClassifier;
        private string mSubClassifier;
        private string FullClassifierPath {
            get { return mClassifier + "_" + mSubClassifier; }
        }
        private bool IsValidClassifier {
            get { return mClassifier != null && mClassifier.Length > 0 && mSubClassifier != null; }
        }

        // Keep the current instance
        private static AirSigManager sInstance;

        /// Event handler for receiving common gesture matching result
        public delegate void OnGestureDrawStart(bool start);
        public event OnGestureDrawStart onGestureDrawStart;

        /// Event handler for receiving custom predefined gesture matching result
        public delegate void OnDeveloperDefinedMatch(long gestureId, string gesture, float score);
        public event OnDeveloperDefinedMatch onDeveloperDefinedMatch;

        /// Event handler for receiving user gesture matching result
        public delegate void OnPlayerSignatureMatch(long gestureId, bool match, int targetIndex);
        public event OnPlayerSignatureMatch onPlayerSignatureMatch;

        /// Event handler for receiving smart identify of predefined gesture matching result
        public delegate void OnSmartIdentifyDeveloperDefinedMatch(long gestureId, string gesture);
        public event OnSmartIdentifyDeveloperDefinedMatch onSmartIdentifyDeveloperDefinedMatch;

        /// Event handler for receiving triggering of a gesture
        public class GestureTriggerEventArgs : EventArgs {
            public bool Continue { get; set; }
            public Mode Mode { get; set; }
            public List<int> Targets { get; set; }
        }
        public delegate void OnGestureTriggered(long gestureId, GestureTriggerEventArgs eventArgs);
        public event OnGestureTriggered onGestureTriggered;

        /// Event handler for receiving training result
        public delegate void OnPlayerSignatureTrained(long gestureId, Error error, float progress, SecurityLevel securityLevel);
        public event OnPlayerSignatureTrained onPlayerSignatureTrained;

        /// Event handler for receiving custom gesture result
        public delegate void OnPlayerGestureAdd(long gestureId, Dictionary<int, int> count);
        public event OnPlayerGestureAdd onPlayerGestureAdd;

        /// Event handler for receiving custom gesture result
        public delegate void OnPlayerGestureMatch(long gestureId, int match);
        public event OnPlayerGestureMatch onPlayerGestureMatch;


        // Callback definition for bridging library
        private delegate void DataCallback(IntPtr buffer, int length, int entryLength);
        private DataCallback _DataCallbackHolder;

        private delegate void MovementCallback(int controller, int type);
        private MovementCallback _MovementCallbackHolder;

        private delegate void IdentifySigResult(IntPtr match, IntPtr error, int numberOfTimesCanTry, int secondsToReset);

        private delegate void VerifyPredGesResult(IntPtr gestureObj, float score, float conf);

        private delegate void AddSigResult(IntPtr action, IntPtr error, float progress, IntPtr securityLevel);

        private delegate void VerifyGesResult(IntPtr gesture, float score, float conf);

        // Load in exported functions
        [DllImport("AirsigBridgeDll")]
        private static extern IntPtr GetControllerHelperObject(byte[] buf, int length);

        [DllImport("AirsigBridgeDll")]
        private static extern IntPtr GetControllerHelperObjectWithConfig(byte[] buf, int length, float f1, float f2);

        [DllImport("AirsigBridgeDll")]
        private static extern void Shutdown(IntPtr obj);

        [DllImport("AirsigBridgeDll")]
        private static extern void SetSensorDataCallback(IntPtr obj, DataCallback callback);

        [DllImport("AirsigBridgeDll")]
        private static extern void SetMovementCallback(IntPtr obj, MovementCallback callback);

        [DllImport("AirsigBridgeDll")]
        private static extern void TestIn(IntPtr obj, float[] data, int length, int entryLength);

        [DllImport("AirsigBridgeDll")]
        private static extern int GetActionIndex(IntPtr action);

        [DllImport("AirsigBridgeDll")]
        private static extern int GetErrorType(IntPtr error);

        [DllImport("AirsigBridgeDll")]
        private static extern void IdentifySignature(IntPtr obj, float[] data, int length, int entryLength, int[] targetIndex, int indexLength, IdentifySigResult callback);

        [DllImport("AirsigBridgeDll")]
        private static extern void AddSignature(IntPtr obj, int index, float[] data, int length, int entryLength, AddSigResult callback);

        [DllImport("AirsigBridgeDll")]
        private static extern bool DeleteAction(IntPtr obj, int index);

        [DllImport("AirsigBridgeDll")]
        private static extern void VerifyGesture(IntPtr obj, int[] indexes, int indexesLength, float[] data, int numDataEntry, int entryLength, VerifyGesResult callback);

        [DllImport("AirsigBridgeDll")]
        private static extern void VerifyPredefinedGesture(IntPtr obj,
            byte[] classifier, int classifierLength,
            byte[] subClassifier, int subClassifierLength,
            byte[] targetGestureNames, int[] eachTargetNameLength, int numTargetGesture,
            float[] data, int dataLength, int dataEntryLength,
            VerifyPredGesResult callback);

        [DllImport("AirsigBridgeDll")]
        private static extern void GetResultGesture(IntPtr gestureObj, byte[] buf, int length);

        [DllImport("AirsigBridgeDll")]
        private static extern int GetASGesture(IntPtr gesture);

        [DllImport("AirsigBridgeDll")]
        private static extern bool IsTwoGestureSimilar(IntPtr obj, float[] data1, int numData1Entry, int data1EntryLength, float[] data2, int numData2Entry, int data2EntryLength);

        [DllImport("AirsigBridgeDll")]
        private static extern void SetCustomGesture(IntPtr obj, int gestureIndex, float[] dataArray, int arraySize, int[] numDataEntry, int[] dataEntryLength);

        [DllImport("AirsigBridgeDll")]
        private static extern IntPtr IdentifyCustomGesture(IntPtr obj, int[] indexes, int indexesLength, float[] data, int numDataEntry, int entryLength);

        [DllImport("AirsigBridgeDll")]
        private static extern void SetCustomGestureStr(IntPtr obj, byte[] gestureId, int gestureIdLength, float[] dataArray, int arraySize, int[] numDataEntry, int[] dataEntryLength);

        [DllImport("AirsigBridgeDll")]
        private static extern IntPtr IdentifyCustomGestureStr(IntPtr obj, byte[] gestureIds, int numGesture, int[] gestureLength, float[] data, int numDataEntry, int entryLength);

        [DllImport("AirsigBridgeDll")]
        private static extern bool IsCustomGestureExisted(IntPtr obj, float[] data, int numDataEntry, int entryLength);

        [DllImport("AirsigBridgeDll")]
        private static extern int GetASCustomGestureRecognizeGestureInt(IntPtr gesture);

        [DllImport("AirsigBridgeDll")]
        private static extern float GetASCustomGestureRecognizeGestureConfidence(IntPtr gesture);

        [DllImport("AirsigBridgeDll")]
        private static extern void GetASCustomGestureRecognizeGestureStr(IntPtr gesture, byte[] buf, int bufLength);

        [DllImport("AirsigBridgeDll")]
        private static extern void DeleteASCustomGestureRecognizeGesture(IntPtr gesture);

        IntPtr vrControllerHelper = IntPtr.Zero;
        // ========================================================================

        // Use to get ID of a gesture
        private static readonly DateTime InitTime = DateTime.UtcNow;
        private static long GetCurrentGestureID() {
            return (long)(DateTime.UtcNow - InitTime).TotalMilliseconds;
        }

        // Train fail accumlative Count
        private int mTrainFailCount = 0;

        // security level too low count
        private static int mSecurityTooLowCount = 0;

        // New training API
        private const int TRAINING_STEP = 5;
        //private float[] mTrainingProgress = new float[TRAINING_STEP] {
        //    0.2f, 0.4f, 0.6f, 0.8f, 1.0f
        //};


        private List<float[]> mTrainingProgressGestures = new List<float[]>();

        // Cache for recent used sensor data
        private const int CACHE_SIZE = 10;

        private SortedDictionary<long, float[]> mCache = new SortedDictionary<long, float[]>();


        // Data structure for saving current training status
        private TrainData mTrainData = new TrainData();
        //private bool mHasTrainDataChanged = false;

        // To handle short signature when setup 
        private static int mFirstTrainDataSize = 0;
        private const float TRAIN_DATA_THRESHOLD_RATIO = 0.65f;

        // Custom gesture result
        public class CustomGestureResult<T> {
            public T bestMatch;
            public float confidence;
        }


        // Cache for AddPlayerGesture, where int is actionIndex and List<float[]> is sensorData
        private Dictionary<int, List<float[]>> mCustomGestureCache = new Dictionary<int, List<float[]>>();

        // Smart training sensor data of a same target
        private SortedList<float, float[]> mSmartTrainCache = new SortedList<float, float[]>();
        private class SmartTrainActionBundle {
            public List<float[]> cache;
            public int targetIndex;
            public int nextIndex;
            public float progress;
            public SmartTrainActionBundle(int targetIndex, List<float[]> cache) {
                this.targetIndex = targetIndex;
                this.cache = cache;
                this.nextIndex = cache.Count - 1; // starting from the last element
                this.progress = 0f;
            }
        }
        private class SmartTrainPredefinedActionBundle {
            public List<float[]> cache;
            public string targetGesture;
            public int nextIndex;
            public float progress;
            public SmartTrainPredefinedActionBundle(string targetPredefined, List<float[]> cache) {
                this.targetGesture = targetPredefined;
                this.cache = cache;
                this.nextIndex = cache.Count - 1; // starting from the last element
                this.progress = 0f;
            }
        }
        // ========================================================================

        // For storing smart identify result
        private class IdentifyActionBundle {
            public long id;
            public int basedIndex;
            public int matchIndex;
            public string type;
            public float score;
            public float conf;
            public float[] sensorData;
            public IdentifyActionBundle(long gestureId, int basedIndex, float[] sensorData) {
                this.id = gestureId;
                this.basedIndex = basedIndex;
                this.sensorData = sensorData;
                this.score = 0f;
            }
        }

        private class IdentifyPredefinedActionBundle {
            public long id;
            public string basedGesture;
            public string matchGesture;
            public string type;
            public float score;
            public float conf;
            public float[] sensorData;
            public IdentifyPredefinedActionBundle(long gestureId, string basedGesture, float[] sensorData) {
                this.id = gestureId;
                this.basedGesture = basedGesture;
                this.sensorData = sensorData;
                this.score = 0f;
            }
        }

        // For internal algorithm picking proirity
        private class ErrorCount {
            public int commonErrCount = 0;
            public int userErrCount= 0;
        }

        private class Confidence {
            public float commonConfidence = 0;
            public float userConfidence = 0;
        }

        // Store all Smart Gesture's error count stat
        private class CommonSmartGestureStat {
            public bool isStatExist = false;
            public Dictionary<int, ErrorCount> gestureStat = new Dictionary<int, ErrorCount>();
            public Dictionary<int, Confidence> gestureConf = new Dictionary<int, Confidence>();

            public void checkThenAdd(int index) {
                if( ! gestureStat.ContainsKey(index)) {
                    gestureStat[index] = new ErrorCount();
                }
                if( ! gestureConf.ContainsKey(index)) {
                    gestureConf[index] = new Confidence();
                }
            }
        }
        private CommonSmartGestureStat mCommonGestureStat = new CommonSmartGestureStat();

        private class PredefinedSmartGestureStat {
            public bool isStatExist = false;
            public Dictionary<string, ErrorCount> gestureStat = new Dictionary<string, ErrorCount>();
            public Dictionary<string, Confidence> gestureConf = new Dictionary<string, Confidence>();

            public void checkThenAdd(string index) {
                if (!gestureStat.ContainsKey(index)) {
                    gestureStat[index] = new ErrorCount();
                }
                if (!gestureConf.ContainsKey(index)) {
                    gestureConf[index] = new Confidence();
                }
            }
        }
        private Dictionary<string, PredefinedSmartGestureStat> mPredGestureStatDict = new Dictionary<string, PredefinedSmartGestureStat>();

        // Store all cache of smart training
        private Dictionary<string, List<float[]>> mSmartTrainPredefinedCacheCollection = new Dictionary<string, List<float[]>>();
        // ========================================================================

        private float mNotifyScoreThreshold = -0.5f;
        /// The minimum score value for common gesture to notify the receiver
        public float NotifyScoreThreshold {
            get {
                return mNotifyScoreThreshold;
            }
            set {
                mNotifyScoreThreshold = value;
            }
        }

        public bool IsDbExist {
            get {
                string dbPath = Application.streamingAssetsPath + "/";
                if (System.IO.Directory.Exists(dbPath)) {
                    string[] files = System.IO.Directory.GetFiles(dbPath, "airsig_*.db", System.IO.SearchOption.TopDirectoryOnly);
                    if (files.Length <= 0) {
                        return false;
                    } else {
                        return true;
                    }
                }
                return false;
            }
           
        }

        public bool IsLowSecureSignature {
            get {
                return mSecurityTooLowCount > 2;
            }
        }

        void OnSensorStartDraw(bool enable) {
            if (null != onGestureDrawStart) {
                sInstance.onGestureDrawStart(enable);
            }
        }

        /// Delete a trained player's gesture or signature
        public void DeletePlayerRecord(int targetIndex) {
            DeleteAction(vrControllerHelper, targetIndex);
        }

        private void TrainUserGesture(long id, int targetIndex, float[] sensorData, Action<SmartTrainActionBundle> furtherAction, SmartTrainActionBundle bundle) {
            AddSignature(vrControllerHelper, targetIndex, sensorData, sensorData.Length / 10, 10,
                (IntPtr action, IntPtr error, float progress, IntPtr securityLevel) => {
                    if (progress == 0.2f) {
                        if (bundle.cache.Count() > 0 && mFirstTrainDataSize == 0) {
                            mFirstTrainDataSize = bundle.cache[bundle.cache.Count() - 1].Length / 10;
                            if (EnableDebugLog) Debug.Log("[AirSigManager][OnAddUserGestureListener] First train data size: " + mFirstTrainDataSize);
                        }
                    } else if (progress >= 1.0f) {
                        mFirstTrainDataSize = 0;
                        mSecurityTooLowCount = 0;
                    } else {
                        if (bundle.cache.Count() > 0) {
                            int size = bundle.cache[bundle.cache.Count() - 1].Length / 10;
                            if (EnableDebugLog) Debug.Log("2nd+ train size: " + size);
                        }
                    }

                    if (null == sInstance.onPlayerSignatureTrained) {
                        if (EnableDebugLog) Debug.Log("[AirSigManager][TrainUserGesture] Listener for onPlayerSignatureTrained does not exist");
                        return;
                    }
                    int errorCode = 0;
                    if (IntPtr.Zero != error) {
                        errorCode = GetErrorType(error);
                    }
                    if (0 != errorCode) {
                        if (EnableDebugLog) Debug.Log(string.Format("[AirSigManager][TrainUserGesture] Add Signature({0}) Fail Due to - {1}",
                            bundle.targetIndex,
                            errorCode));
                        if (EnableDebugLog) Debug.Log(string.Format("[AirSigManager][TrainUserGesture] Progress:{0}  FailCount:{1}",
                            progress,
                            sInstance.mTrainFailCount));

                        int size = bundle.cache[bundle.cache.Count() - 1].Length / 10;
                        if (EnableDebugLog) Debug.Log("[AirSigManager][TrainUserGesture] ***<< size=" + size + " >>***");

                        if (size > COMMON_MISTOUCH_THRESHOLD) {
                            if (bundle.cache.Count() > 0 && mFirstTrainDataSize > 0 && progress >= 0.2f) {
                                // it's not first train data
                                if (size >= mFirstTrainDataSize * TRAIN_DATA_THRESHOLD_RATIO) {
                                    // size is less than threshold of the first data
                                    if (progress >= 0.5f) {
                                        // If user has progressed more than 3 times, add cumulative count
                                        if (EnableDebugLog) Debug.Log("[AirSigManager][TrainUserGesture] << Don't write consitant signature on purpose >>");
                                        sInstance.mTrainFailCount++;
                                        sInstance.onPlayerSignatureTrained(
                                            id,
                                            new Error(errorCode, ""),
                                            progress,
                                            0); //securityLevel == null ? (SecurityLevel)0 : (SecurityLevel)securityLevel.Get<int> ("level"));
                                    }
                                } else {
                                    // mistouch for 2nd+ touch
                                    if (EnableDebugLog)
                                        Debug.Log("[AirSigManager][TrainUserGesture] << Use Sign too few word for mistouch >>");
                                    sInstance.mTrainFailCount++;
                                    sInstance.onPlayerSignatureTrained(
                                        id,
                                        new Error(Error.SIGN_TOO_FEW_WORD, "Sign too few words"),
                                        progress, // progress stays same
                                        0); //securityLevel == null ? (SecurityLevel)0 : (SecurityLevel)securityLevel.Get<int> ("level"));
                                }

                                if (sInstance.mTrainFailCount >= 3 || progress < 0.5f) {
                                    // Reset if any error
                                    sInstance.DeletePlayerRecord(bundle.targetIndex);
                                    // Report error
                                    sInstance.onPlayerSignatureTrained(
                                        id,
                                        new Error(errorCode, ""),
                                        0f,
                                        0); //securityLevel == null ? (SecurityLevel)0 : (SecurityLevel)securityLevel.Get<int> ("level"));

                                    // Reset will also reset cumulative count
                                    sInstance.mTrainFailCount = 0;
                                } else if (progress >= 0.5f) {
                                    sInstance.onPlayerSignatureTrained(
                                        id,
                                        new Error(errorCode, ""),
                                        progress,
                                        0); //securityLevel == null ? (SecurityLevel)0 : (SecurityLevel)securityLevel.Get<int> ("level"));
                                }
                            } else {
                                sInstance.onPlayerSignatureTrained(
                                    id,
                                    new Error(errorCode, ""),
                                    0f,
                                    0); //securityLevel == null ? (SecurityLevel)0 : (SecurityLevel)securityLevel.Get<int> ("level"));
                            }
                        } else {
                            // Less sample than the threshold, consider a mistouch
                            //sInstance.onPlayerSignatureTrained(
                            //    id,
                            //    new Error(Error.SIGN_WITH_MISTOUCH, "Sign with mistouch"),
                            //    progress,
                            //    0);
                        }
                    } else if (IntPtr.Zero != action) {
                        if (EnableDebugLog) {
                            Debug.Log("[AirSigManager][TrainUserGesture] Add Signature status:" + GetActionIndex(action) +
                                ", progress:" + progress +
                                ", securityLevel: NOT IMPLEMENTED");
                        }

                        sInstance.onPlayerSignatureTrained(
                            id,
                            errorCode == 0 ? null : new Error(errorCode, ""),
                            progress,
                            0); //securityLevel == null ? (SecurityLevel)0 : (SecurityLevel)securityLevel.Get<int>("level"));

                        // A pass should reset the cumulative 
                        sInstance.mTrainFailCount = 0;
                        mSecurityTooLowCount = 0;
                    }

                    if (null != furtherAction && null != bundle) {
                        bundle.progress = progress;
                        furtherAction(bundle);
                    }
                });

        }

        private void SmartTrainPredefinedGesture(string target) {
            if (EnableDebugLog) Debug.Log("[AirSigManager][SmartTrainDeveloperDefined] target:" + target + " mSmartTrainCache.Count:" + mSmartTrainCache.Count);
            if (mSmartTrainCache.Count >= 3) { // we need minimum of 3 gesture to complete the training
                List<float[]> cache = new List<float[]>(mSmartTrainCache.Values);
                List<float> keys = new List<float>(mSmartTrainCache.Keys);
                keys.Reverse();
                cache.Reverse();
                if (EnableDebugLog) Debug.Log("[AirSigManager][SmartTrainDeveloperDefined] target: " + target + ", cacheOrder:" + string.Join(", ", keys.Select(x => x.ToString()).ToArray()));
                SmartTrainPredefinedGesture2(new SmartTrainPredefinedActionBundle(target, cache));
                // Store current smart train cache for later process
                mSmartTrainPredefinedCacheCollection.Add(target, cache);
            }
            mSmartTrainCache.Clear();
        }

        private void SmartTrainGestures(SmartTrainActionBundle bundle) {
            if (bundle.nextIndex >= 0) {
                float[] sensorData = bundle.cache[bundle.nextIndex];
                bundle.nextIndex--;
                if (EnableDebugLog) Debug.Log("[SmartTrainGestures] bundle.nextIndex: " + bundle.nextIndex);
                AddCustomGesture(sensorData);
                SmartTrainGestures(bundle);
            } else {
                try {
                    mTrainData.useUserGesture.Add(bundle.targetIndex, true);
                } catch (ArgumentException) {
                    mTrainData.useUserGesture[bundle.targetIndex] = true;
                }

                int totalLength = 0;
                int[] numDataEntryList = new int[mTrainingProgressGestures.Count()];
                int[] dataEntryLengthList = new int[mTrainingProgressGestures.Count()];
                for (int i = 0; i < mTrainingProgressGestures.Count(); i++) {
                    totalLength += mTrainingProgressGestures[i].Length;
                    numDataEntryList[i] = mTrainingProgressGestures[i].Length / 10;
                    dataEntryLengthList[i] = 10;
                }
                float[] dataList = new float[totalLength];
                for (int i = 0, k = 0; i < mTrainingProgressGestures.Count(); i++) {
                    float[] entry = mTrainingProgressGestures[i];
                    for (int j = 0; j < entry.Length; j++) {
                        dataList[k] = entry[j];
                        k++;
                    }
                }
                // float[][] dataList = new float[mTrainingProgressGestures.Count()][];
                // int[] numDataEntryList = new int[mTrainingProgressGestures.Count()];
                // int[] dataEntryLengthList = new int[mTrainingProgressGestures.Count()];
                // for(int i = 0; i < mTrainingProgressGestures.Count(); i ++) {
                // 	dataList[i] = mTrainingProgressGestures[i];
                // 	numDataEntryList[i] = mTrainingProgressGestures[i].Length / 10;
                // 	dataEntryLengthList[i] = 10;
                // }
                // add them to the engine
                if (EnableDebugLog) Debug.Log(string.Format("Add gestures ({0} records) to setCustomGesture for index:{1}", mTrainingProgressGestures.Count(), bundle.targetIndex));
                SetCustomGesture(vrControllerHelper, bundle.targetIndex, dataList, mTrainingProgressGestures.Count(), numDataEntryList, dataEntryLengthList);
                mTrainingProgressGestures.Clear();
                if (EnableDebugLog) Debug.Log("[AirSigManager][SmartTrainGestures] Smart Train Completed!!");
            }
        }

        private void SmartTrainPredefinedGesture2(SmartTrainPredefinedActionBundle bundle) {
            if (bundle.nextIndex >= 0) {
                float[] sensorData = bundle.cache[bundle.nextIndex];
                bundle.nextIndex--;
                if (EnableDebugLog) Debug.Log("[SmartTrainPredefinedGestures] bundle.nextIndex: " + bundle.nextIndex);
                AddCustomGesture(sensorData);
                SmartTrainPredefinedGesture2(bundle);
            } else {
                try {
                    mTrainData.usePredefinedUserGesture.Add(bundle.targetGesture, true);
                } catch (ArgumentException) {
                    mTrainData.usePredefinedUserGesture[bundle.targetGesture] = true;
                }

                int totalLength = 0;
                int[] numDataEntryList = new int[mTrainingProgressGestures.Count()];
                int[] dataEntryLengthList = new int[mTrainingProgressGestures.Count()];
                for (int i = 0; i < mTrainingProgressGestures.Count(); i++) {
                    totalLength += mTrainingProgressGestures[i].Length;
                    numDataEntryList[i] = mTrainingProgressGestures[i].Length / 10;
                    dataEntryLengthList[i] = 10;
                }
                float[] dataList = new float[totalLength];
                for (int i = 0, k = 0; i < mTrainingProgressGestures.Count(); i++) {
                    float[] entry = mTrainingProgressGestures[i];
                    for (int j = 0; j < entry.Length; j++) {
                        dataList[k] = entry[j];
                        k++;
                    }
                }
                // add them to the engine
                if (EnableDebugLog) Debug.Log(string.Format("Add gestures ({0} records) to setCustomGesture for index:{1}", mTrainingProgressGestures.Count(), bundle.targetGesture));
                byte[] targetGestureInByte = Encoding.ASCII.GetBytes(bundle.targetGesture);
                SetCustomGestureStr(vrControllerHelper, targetGestureInByte, bundle.targetGesture.Length, dataList, mTrainingProgressGestures.Count(), numDataEntryList, dataEntryLengthList);
                mTrainingProgressGestures.Clear();
                if (EnableDebugLog) Debug.Log("[AirSigManager][SmartTrainPredefinedGestures] Smart Train Completed!!");
            }
        }

        private void SmartIdentifyPredefinedGesture(long id, float[] sensorData) {
            if(null == mCurrentPredefined || mCurrentPredefined.Count == 0) {
                if (EnableDebugLog) Debug.LogWarning("[AirSigManager][SmartIdentifyDeveloperDefined] Identify without target!");
                return;
            }
            if (null == mClassifier || mClassifier.Length == 0) {
                if (EnableDebugLog) Debug.LogWarning("[AirSigManager][SmartIdentifyDeveloperDefined] Identify without classifier!");
                return;
            }
            IdentifyPredefined(id, sensorData, mCurrentPredefined, mClassifier, mSubClassifier, SmartPredefinedIdentifyResult, new IdentifyPredefinedActionBundle(0, "", sensorData), false);
        }

        private void SmartPredefinedIdentifyResult(IdentifyPredefinedActionBundle bundle) {
            if(null != bundle) {
                if(bundle.matchGesture != null && bundle.matchGesture.Length > 0 && bundle.score > 1.0f) {
                    if(IsValidClassifier && mPredGestureStatDict.ContainsKey(FullClassifierPath)) {

                        if (mPredGestureStatDict[FullClassifierPath].gestureStat.ContainsKey(bundle.matchGesture)) {

                            bool isConfidenceFavorCommon = true;
                            if (mPredGestureStatDict[FullClassifierPath].gestureConf.ContainsKey(bundle.matchGesture)) {
                                if (EnableDebugLog) Debug.Log(string.Format("Confidence Level id {0} - common: {1}  user: {2}",
                                     bundle.id,
                                     mPredGestureStatDict[FullClassifierPath].gestureConf[bundle.matchGesture].commonConfidence,
                                     mPredGestureStatDict[FullClassifierPath].gestureConf[bundle.matchGesture].userConfidence));
                                isConfidenceFavorCommon = mPredGestureStatDict[FullClassifierPath].gestureConf[bundle.matchGesture].commonConfidence >=
                                    mPredGestureStatDict[FullClassifierPath].gestureConf[bundle.matchGesture].userConfidence;
                            }
                            bool toCompareCustom = false;
                            if (mPredGestureStatDict[FullClassifierPath].gestureStat[bundle.matchGesture].commonErrCount ==
                                mPredGestureStatDict[FullClassifierPath].gestureStat[bundle.matchGesture].userErrCount && !isConfidenceFavorCommon) {
                                toCompareCustom = true;
                            }

                            Dictionary<string, ErrorCount> gestureStat = mPredGestureStatDict[FullClassifierPath].gestureStat;
                            if (gestureStat[bundle.matchGesture].commonErrCount > gestureStat[bundle.matchGesture].userErrCount ||
                                toCompareCustom) {
                                if (EnableDebugLog) Debug.Log("[AirSigManager][SmartIdentifyDeveloperDefined] try lookup for these keys: " + string.Join(",", mCurrentPredefined.Select(x => x.ToString()).ToArray()));
                                // Verify first before we set to use "user gesture"
                                CustomGestureResult<string> result = IdentifyCustomGesture(bundle.id, bundle.sensorData, mCurrentPredefined);
                                if (result != null && mCurrentPredefined.Contains(result.bestMatch)) {
                                    if (result.bestMatch == bundle.matchGesture) {
                                        // found user and common match the same target
                                        sInstance.onSmartIdentifyDeveloperDefinedMatch(bundle.id, result.bestMatch);
                                    } else {
                                        // found not equal
                                        if (gestureStat.ContainsKey(result.bestMatch)) {
                                            if (gestureStat[result.bestMatch].commonErrCount < gestureStat[result.bestMatch].userErrCount) {
                                                // delimma, use common to ensure not worse than before
                                                sInstance.onSmartIdentifyDeveloperDefinedMatch(bundle.id, bundle.matchGesture);
                                                return;
                                            }
                                        }
                                        // fallback to to user define if no error count found or user error count is less than common error count
                                        sInstance.onSmartIdentifyDeveloperDefinedMatch(bundle.id, result.bestMatch);
                                    }
                                    return;
                                }
                                // custom gesture result invalid, use predefined result
                                sInstance.onSmartIdentifyDeveloperDefinedMatch(bundle.id, bundle.matchGesture);
                                return;
                            }
                        }
                    }
                    // no comparing stat or comparing stat cannot tell which one is worse,
                    // just report back the builtin predefined result
                    sInstance.onSmartIdentifyDeveloperDefinedMatch(bundle.id, bundle.matchGesture);
                } else {
                    if (EnableDebugLog) Debug.Log("[AirSigManager][SmartIdentifyDeveloperDefined] common identify failed, try lookup 'user gesture'");
                    //Dictionary<string, bool>.KeyCollection keyColl = mTrainData.usePredefinedUserGesture.Keys;
                    if (mCurrentPredefined.Count > 0) {
                        if (EnableDebugLog) Debug.Log("[AirSigManager][SmartIdentifyDeveloperDefined] try lookup for these keys: " + string.Join(",", mCurrentPredefined.Select(x => x.ToString()).ToArray()));
                        // Verify first before we set to use "user gesture"
                        //List<string> keys = keyColl.ToList();
                        if (EnableDebugLog) Debug.Log("[AirSigManager][SmartIdentifyDeveloperDefined] against index: " + string.Join(",", mCurrentPredefined.Select(x => x.ToString()).ToArray()));
                        //string result = IdentifyPlayerGesture(bundle.id, bundle.sensorData, mCurrentPredefined);
                        CustomGestureResult<string> result = IdentifyCustomGesture(bundle.id, bundle.sensorData, mCurrentPredefined);
                        if (result != null) {
                            sInstance.onSmartIdentifyDeveloperDefinedMatch(bundle.id, result.bestMatch);
                        } else {
                            sInstance.onSmartIdentifyDeveloperDefinedMatch(bundle.id, "");
                        }
                    } else {
                        if (EnableDebugLog) Debug.Log("[AirSigManager][SmartIdentifyDeveloperDefined] no user gesture to lookup!");
                        sInstance.onSmartIdentifyDeveloperDefinedMatch(bundle.id, "");
                    }
                }
            }
        }

        public void IdentifyUserGesture(float[] sensorData) {
            IdentifyUserGesture(0, sensorData, new int[] { 101 }, null, null, true);
        }

        private void IdentifyUserGesture(long id, float[] sensorData, int[] targetIndex, Action<IdentifyActionBundle> furtherAction, IdentifyActionBundle bundle, bool notifyObserver) {
            IdentifySignature(vrControllerHelper, sensorData, sensorData.Length / 10, 10, targetIndex, targetIndex.Length,
                (IntPtr match, IntPtr error, int numberOfTimesCanTry, int secondsToReset) => {
                    if (null == sInstance.onPlayerSignatureMatch) {
                        if (EnableDebugLog) Debug.Log("[AirSigManager][UserGesture] Listener for not onPlayerSignatureMatch does not exist");
                        return;
                    }
                    int errorCode = 0;
                    if (IntPtr.Zero != error) {
                        errorCode = GetErrorType(error);
                    }
                    if (0 != errorCode) {
                        if (EnableDebugLog) Debug.Log("[AirSigManager][UserGesture] Identify Fail: code-" + errorCode);
                        // no match
                        if (notifyObserver) {
                            sInstance.onPlayerSignatureMatch(id, false, 0);
                        }
                        if (null != furtherAction && null != bundle) {
                            bundle.matchIndex = -1;
                            bundle.type = "user";
                            furtherAction(bundle);
                        }
                    } else if (IntPtr.Zero != match) {
                        int actionIndex = GetActionIndex(match);
                        if (EnableDebugLog) Debug.Log("[AirSigManager][UserGesture] Identify Pass: " + actionIndex);
                        if (notifyObserver) {
                            sInstance.onPlayerSignatureMatch(id, true, actionIndex);
                        }
                        if (null != furtherAction && null != bundle) {
                            bundle.matchIndex = actionIndex;
                            bundle.type = "user";
                            furtherAction(bundle);
                        }
                    }
                });
        }

        private void IdentifyUserGesture(long id, float[] sensorData, int[] targetIndex) {
            IdentifyUserGesture(id, sensorData, targetIndex, null, null, true);
        }

        static char[] TAIL_TO_REMOVE = Encoding.ASCII.GetString(new byte[] { (byte)254 }).ToCharArray();
        static char[] NULL_TO_REMOVE = Encoding.ASCII.GetString(new byte[] { (byte)0 }).ToCharArray();
        private void IdentifyPredefined(long id, float[] sensorData, List<string> targetGesture, string classifier, string subClassifier) {
            IdentifyPredefined(id, sensorData, targetGesture, classifier, subClassifier, null, null, true);
        }

        private void IdentifyPredefined(long id, float[] sensorData, List<string> targetGesture, string classifier, string subClassifier, Action<IdentifyPredefinedActionBundle> furtherAction, IdentifyPredefinedActionBundle bundle, bool toInvokeCommonObserver) {
            byte[] tmpClassifierInByte = Encoding.ASCII.GetBytes(classifier);
            byte[] classifierInByte = new byte[tmpClassifierInByte.Length];
            Array.Copy(tmpClassifierInByte, classifierInByte, tmpClassifierInByte.Length);
            byte[] subClassifierInByte = Encoding.ASCII.GetBytes(subClassifier);

            int totalLength = 0;
            int[] targetGestureLength = new int[targetGesture.Count];
            for (int i = 0; i < targetGesture.Count; i++) {
                totalLength += targetGesture[i].Length;
                targetGestureLength[i] = targetGesture[i].Length;
            }
            byte[] targetGestureInByte = new byte[totalLength];

            int offset = 0;
            for(int i = 0; i < targetGesture.Count; i ++) {
                byte[] tmp = Encoding.ASCII.GetBytes(targetGesture[i]);
                Array.Copy(tmp, 0, targetGestureInByte, offset, targetGesture[i].Length);
                offset += targetGesture[i].Length;
            }
            VerifyPredefinedGesture(vrControllerHelper,
                classifierInByte, classifierInByte.Length,
                subClassifierInByte, subClassifierInByte.Length,
                targetGestureInByte, targetGestureLength, targetGesture.Count,
                sensorData, sensorData.Length / 10, 10,
                (IntPtr gestureObj, float score, float conf) => {
                    byte[] buf = Enumerable.Repeat<byte>(0, 1024).ToArray();
                    GetResultGesture(gestureObj, buf, 1024);
                    string gesture = Encoding.ASCII.GetString(buf)
                        .TrimEnd(TAIL_TO_REMOVE)
                        .TrimEnd(NULL_TO_REMOVE);
                    if (EnableDebugLog) Debug.Log("[AirSigManager][DeveloperDefined] match: " + gesture + ", score: " + score + ", conf: " + conf);
                    if (null != furtherAction && null != bundle) {
                        bundle.matchGesture = gesture;
                        bundle.score = score;
                        bundle.type = "common";
                        bundle.conf = conf;
                        furtherAction(bundle);
                    }
                    if (toInvokeCommonObserver) {
                        if (null != sInstance.onDeveloperDefinedMatch) {
                            sInstance.onDeveloperDefinedMatch(id, gesture, score);
                        } else {
                            if (EnableDebugLog) Debug.Log("[AirSigManager][DeveloperDefined] Listener for onDeveloperDefinedMatch does not exist!");
                        }
                    }
                });
        }

        void onSensorDataRecorded(IntPtr buffer, int length, int entryLength) {
            long id = GetCurrentGestureID();
            if (EnableDebugLog) Debug.Log("gesture - id: " + id + ", length: " + length + ", entryLength: " + entryLength + " received");
            int totalLength = length * entryLength;
            float[] data = new float[totalLength];
            Marshal.Copy(buffer, data, 0, totalLength);
            AddToCache(id, data);
            if (null != sInstance.onGestureTriggered) {
                GestureTriggerEventArgs eventArgs = new GestureTriggerEventArgs();
                eventArgs.Continue = true;
                eventArgs.Mode = mCurrentMode;
                eventArgs.Targets = mCurrentTarget.Select(item => item).ToList<int>();
                sInstance.onGestureTriggered(id, eventArgs);
                if (eventArgs.Continue) {
                    PerformActionWithGesture(eventArgs.Mode, eventArgs.Targets, id, data);
                }
            } else {
                PerformActionWithGesture(mCurrentMode, mCurrentTarget, id, data);
            }

        }

        void onMovementDetected(int controller, int type) {
            OnSensorStartDraw(type == 0);
        }

        void AddToCache(long id, float[] sensorData) {
            while (mCache.Count >= CACHE_SIZE) {
                KeyValuePair<long, float[]> instance = mCache.First();
                mCache.Remove(instance.Key);
            }
            mCache.Add(id, sensorData);
        }

        public float[] GetFromCache(long id) {
            if (mCache.ContainsKey(id)) {
                return mCache[id];
            }
            return null;
        }

        KeyValuePair<long, float[]> GetLastFromCache() {
            if (mCache.Count > 0) {
                return mCache.Last();
            }
            return default(KeyValuePair<long, float[]>);
        }

        public void PerformActionWithGesture(Mode action, List<int> targets, long gestureId, float[] sensorData) {
            if (null == sensorData || 0 == sensorData.Length) {
                if (EnableDebugLog) Debug.Log("[AirSigManager] Sensor data is not available!");
                return;
            }
            if ((action & AirSigManager.Mode.IdentifyPlayerSignature) > 0) {
                if (EnableDebugLog) Debug.Log("[AirSigManager] IdentifyUserGesture for " + gestureId + "...");

                // timeStart = DateTime.UtcNow;
                IdentifyUserGesture(gestureId, sensorData, targets.ToArray());
            }
            if ((action & AirSigManager.Mode.TrainPlayerSignature) > 0) {
                if (EnableDebugLog) Debug.Log("[AirSigManager] Train for " + gestureId + "...");
                TrainUserGesture(gestureId, targets.First(), sensorData, null, new SmartTrainActionBundle(targets.First(), new List<float[]>() { sensorData }));
            }
            if ((action & AirSigManager.Mode.AddPlayerGesture) > 0) {
                if (EnableDebugLog) Debug.Log("[AirSigManager] CustomGesture for " + gestureId + "...");
                AddCustomGesture(gestureId, sensorData, targets);
            }
            if ((action & AirSigManager.Mode.IdentifyPlayerGesture) > 0) {
                if (EnableDebugLog) Debug.Log("[AirSigManager] IdentifyPlayerGesture for " + gestureId + "...");
                IdentifyCustomGesture(gestureId, sensorData, targets.ToArray(), true);
            }
            
            // Oculus Rift TODO
            if ((action & AirSigManager.Mode.DeveloperDefined) > 0) {
                if (EnableDebugLog) Debug.Log("[AirSigManager] DeveloperDefined for " + gestureId + "...");
                if (mClassifier == null || mClassifier.Length == 0) {
                    Debug.LogWarning("Empty classifiers are provided for DeveloperDefined! No identification will be performed!");
                    return;
                }
                IdentifyPredefined(gestureId, sensorData, mCurrentPredefined, mClassifier, mSubClassifier);
            }
            if((action & AirSigManager.Mode.SmartIdentifyDeveloperDefined) > 0) {
                if (EnableDebugLog) Debug.Log("[AirSigManager] SmartIdentifyDeveloperDefined for " + gestureId + "...");
                SmartIdentifyPredefinedGesture(gestureId, sensorData);
            }
            if((action & AirSigManager.Mode.SmartTrainDeveloperDefined) > 0) {
                if (EnableDebugLog) Debug.Log("[AirSigManager] Add SmartTrain cache for " + gestureId + "...");
                IdentifyPredefined(gestureId, sensorData, new List<string>() { mCurrentPredefined.First() }, mClassifier, mSubClassifier, SmartTrainPredefinedFilterData, new IdentifyPredefinedActionBundle(gestureId, mCurrentPredefined.First(), sensorData), true);
            }
        }

        private void SmartTrainPredefinedFilterData(IdentifyPredefinedActionBundle bundle) {
            if (mSmartTrainCache.Count > 0) {
                if (bundle.score > SMART_TRAIN_PASS_THRESHOLD) {
                    if (EnableDebugLog) Debug.Log(string.Format("[AirSigManager][SmartTrainDeveloperDefined] Add gesture for smart train ID:{0} Score:{1}", bundle.id, bundle.score));
                    float key = bundle.score;
                    while (mSmartTrainCache.ContainsKey(key)) {
                        key += 0.0001f;
                    }
                    mSmartTrainCache.Add(key, bundle.sensorData);
                }
            } else {
                // The 1st data must be greater than COMMON_PASS_THRESHOLD
                if (bundle.score > SMART_TRAIN_PASS_THRESHOLD) {
                    if (EnableDebugLog) Debug.Log(string.Format("[AirSigManager][SmartTrainDeveloperDefined] Add gesture for smart train ID:{0} Score:{1}", bundle.id, bundle.score));
                    float key = bundle.score;
                    mSmartTrainCache.Add(key, bundle.sensorData);
                }
            }
        }

        private void SmartTrainFilterData (IdentifyActionBundle bundle) {
            if (mSmartTrainCache.Count > 0) {
                if (bundle.score > SMART_TRAIN_PASS_THRESHOLD) {
                    if (EnableDebugLog) Debug.Log (string.Format ("[AirSigManager][SmartTrain] Add gesture for smart train ID:{0} Score:{1}", bundle.id, bundle.score));
                    float key = bundle.score;
                    while (mSmartTrainCache.ContainsKey (key)) {
                        key += 0.0001f;
                    }
                    mSmartTrainCache.Add (key, bundle.sensorData);
                }
            } else {
                // The 1st data must be greater than COMMON_PASS_THRESHOLD
                if (bundle.score > SMART_TRAIN_PASS_THRESHOLD) {
                    if (EnableDebugLog) Debug.Log (string.Format ("[AirSigManager][SmartTrain] Add gesture for smart train ID:{0} Score:{1}", bundle.id, bundle.score));
                    float key = bundle.score;
                    mSmartTrainCache.Add (key, bundle.sensorData);
                }
            }
        }

        public void PerformActionWithGesture (Mode action, List<int> targets, long gestureId) {
            PerformActionWithGesture (action, targets, gestureId, GetFromCache (gestureId));
        }

        public bool IsPlayerGestureExisted(float[] floatArray) {
            return IsCustomGestureExisted(vrControllerHelper, floatArray, floatArray.Length / 10, 10);
        }

        public CustomGestureResult<int> IdentifyCustomGesture (long id, float[] floatArray, int[] targets, bool notify) {
            //int match = IdentifyCustomGesture (vrControllerHelper, targets, targets.Length, floatArray, floatArray.Length / 10, 10);
            IntPtr result = IdentifyCustomGesture(vrControllerHelper, targets, targets.Length, floatArray, floatArray.Length / 10, 10);
            int match = GetASCustomGestureRecognizeGestureInt(result);
            float conf = GetASCustomGestureRecognizeGestureConfidence(result);
            DeleteASCustomGestureRecognizeGesture(result);
            if (EnableDebugLog) Debug.Log(string.Format("[AirSigManager][IdentifyPlayerGesture] IdentifyPlayerGesture match: {0}, conf: {1}", match, conf));
            if (notify) {
                if(null != sInstance.onPlayerGestureMatch) {
                    sInstance.onPlayerGestureMatch(id, match);
                }
            }
            CustomGestureResult<int> resultMatch = new CustomGestureResult<int>();
            resultMatch.bestMatch = match;
            resultMatch.confidence = conf;
            return resultMatch;
        }

        public CustomGestureResult<string> IdentifyCustomGesture(long id, float[] floatArray, List<string> targetGesture) {
            int totalLength = 0;
            int[] targetGestureLength = new int[targetGesture.Count];
            for (int i = 0; i < targetGesture.Count; i++) {
                totalLength += targetGesture[i].Length;
                targetGestureLength[i] = targetGesture[i].Length;
            }
            byte[] targetGestureInByte = new byte[totalLength];
            int offset = 0;
            for (int i = 0; i < targetGesture.Count; i++) {
                byte[] tmp = Encoding.ASCII.GetBytes(targetGesture[i]);
                Array.Copy(tmp, 0, targetGestureInByte, offset, targetGesture[i].Length);
                offset += targetGesture[i].Length;
            }
            byte[] buf = Enumerable.Repeat<byte>(0, 1024).ToArray();
            IntPtr result = IdentifyCustomGestureStr(vrControllerHelper, targetGestureInByte, targetGesture.Count, targetGestureLength, floatArray, floatArray.Length / 10, 10);
            GetASCustomGestureRecognizeGestureStr(result, buf, 1024);
            float conf = GetASCustomGestureRecognizeGestureConfidence(result);
            DeleteASCustomGestureRecognizeGesture(result);
            string matchedGesture = Encoding.ASCII.GetString(buf)
                .TrimEnd(TAIL_TO_REMOVE)
                .TrimEnd(NULL_TO_REMOVE);
            if (EnableDebugLog) Debug.Log(string.Format("[AirSigManager][IdentifyPlayerGesture] IdentifyCustomGestureStr match: {0}, conf: {1}", matchedGesture, conf));
            CustomGestureResult<string> resultMatch = new CustomGestureResult<string>();
            resultMatch.bestMatch = matchedGesture;
            resultMatch.confidence = conf;
            return resultMatch;
        }

        // Add to cache only
        private bool AddCustomGesture (long gestureId, float[] data, List<int> targets) {
            Dictionary<int, int> result = new Dictionary<int, int>();
            foreach(int index in targets) {
                if(mCustomGestureCache.ContainsKey(index)) {
                    mCustomGestureCache[index].Add(data);
                }
                else {
                    mCustomGestureCache.Add(index, new List<float[]> { data });
                }
                result.Add(index, mCustomGestureCache[index].Count);
            }
            if (null != sInstance.onPlayerGestureAdd) {
                sInstance.onPlayerGestureAdd(gestureId, result);
            }
            return true;
        }

        private bool AddCustomGesture (float[] floatArray) {
            bool isThisGestureAccepted = false;
            // check with previous data
			if(EnableDebugLog) Debug.Log("===== AddPlayerGesture =====");
            if (mTrainingProgressGestures.Count () == 0) {
                if(EnableDebugLog) Debug.Log (string.Format ("Adding first data..."));
                mTrainingProgressGestures.Add (floatArray);

                isThisGestureAccepted = true;
            } else {
                if(EnableDebugLog) Debug.Log (string.Format ("Data {0}...", mTrainingProgressGestures.Count ()));
                bool hasSimilar = false;
                for (int i = 0; i < mTrainingProgressGestures.Count (); i++) {
                    float[] previous = mTrainingProgressGestures[i];
                    bool isSimilar = IsTwoGestureSimilar (vrControllerHelper, previous, previous.Length / 10, 10, floatArray, floatArray.Length / 10, 10);
                    if(EnableDebugLog) Debug.Log (string.Format ("[{0}]  {1}", i, isSimilar));
                    if (isSimilar) {
                        hasSimilar = true;
                    }
                }
                if (hasSimilar) {
                    mTrainingProgressGestures.Add (floatArray);
                    isThisGestureAccepted = true;
                }
            }
            return isThisGestureAccepted;
        }

        public bool IsTwoGestureSimilar(float[] gesture1, float[] gesture2) {
            return IsTwoGestureSimilar(vrControllerHelper, gesture1, gesture1.Length / 10, 10, gesture2, gesture2.Length / 10, 10);
        }

        public enum Controller {
            RIGHT_HAND = 1,
            LEFT_HAND = 2
        }

        public enum TriggerButton {
            NONE,
            THUMB_STICK_BUTTON,
            SELECT_BUTTON,
            GRASPE_BUTTON,
            TOUCHPAD_BUTTON
        }

        public TriggerButton rightHandStartTrigger = TriggerButton.SELECT_BUTTON;
        public TriggerButton leftHandStartTrigger = TriggerButton.NONE;

        bool isRIndexPressed = false, isRIndexPressedPrev = false;
        bool isRIndexTriggeredUp = false, isRIndexTriggeredDown = false;
        bool isLIndexPressed = false, isLIndexPressedPrev = false;
        bool isLIndexTriggeredUp = false, isLIndexTriggeredDown = false;

        void Update () {
           
        }

        bool mIsInitReady = false;
        void Awake() {
            if (sInstance != null) {
                Debug.LogError("More than one AirSigManager instance was found in your scene. " +
                    "Ensure that there is only one AirSigManager GameObject.");
                this.enabled = false;
                return;
            }
            sInstance = this;

#if WINDOWS_UWP
            string dbPath = ApplicationData.Current.LocalFolder.Path + "/";
            //string dbPath = Application.streamingAssetsPath + "/";

            Task.Run(
            async () =>
            {
                StorageFolder dataFolder = ApplicationData.Current.LocalFolder;
                StorageFolder installedFolder = await Windows.ApplicationModel.Package.Current.InstalledLocation.GetFolderAsync("Data");
                StorageFolder streamingAssetsFolder = await installedFolder.GetFolderAsync("StreamingAssets");

                Debug.Log("data folder: " + dataFolder.Path);
                Debug.Log("streaming assets folder: " + streamingAssetsFolder.Path);

                IReadOnlyList<StorageFile> fileList =
                    await streamingAssetsFolder.GetFilesAsync();
                //IReadOnlyList<StorageFile> fileList =
                //    await dataFolder.GetFilesAsync();
 
                foreach(StorageFile file in fileList)
                {
                    try {
                        await file.CopyAsync(dataFolder, file.Name, NameCollisionOption.FailIfExists);
                    }
                    catch (Exception) {
                    }
                    //Debug.Log("file: " + file.Path);
                }
            }).Wait();
#elif UNITY_EDITOR
            string dbPath = Application.streamingAssetsPath + "/";
#endif
            /*
            if (System.IO.Directory.Exists(dbPath)) { 
                string[] files = System.IO.Directory.GetFiles(dbPath, "airsig_*.db", System.IO.SearchOption.TopDirectoryOnly);
                if (files.Length <= 0) {
                    Debug.LogError("Db files do not exist!");
                    return;
                } else {
                    foreach (string file in files) {
                        Debug.Log("DB file found: " + file);
                    }
                }
            }
            else {
                Debug.LogError("Db folder does not exist!");
                return;
            }
            */

            byte[] path = Encoding.ASCII.GetBytes(dbPath);
            vrControllerHelper = GetControllerHelperObjectWithConfig(path, path.Length, 0.98f, 0.98f);
            if (EnableDebugLog) Debug.Log("DB Path: " + dbPath);
            if (EnableDebugLog) Debug.Log ("IntPtr: " + vrControllerHelper);

            _DataCallbackHolder = new DataCallback (onSensorDataRecorded);
            SetSensorDataCallback (vrControllerHelper, _DataCallbackHolder);

            _MovementCallbackHolder = new MovementCallback(onMovementDetected);
            SetMovementCallback(vrControllerHelper, _MovementCallbackHolder);
            // ====================================================================

            mCache.Clear ();

			Load();
            //Save();

            mIsInitReady = true;

            // Register MS MR controller event
            InteractionManager.InteractionSourceUpdated += InteractionManager_SourceUpdated;
            InteractionManager.InteractionSourceDetected += InteractionManager_SourceDetected;
            InteractionManager.InteractionSourcePressed += InteractionManager_SourcePressed;
            InteractionManager.InteractionSourceReleased += InteractionManager_SourceReleased;
        }

        System.Diagnostics.Stopwatch rStopWatch, lStopWatch;
        void InteractionManager_SourceUpdated(InteractionSourceUpdatedEventArgs args) {
            //Debug.Log(string.Format("R hand collecting: {0},  L hand collecting: {1}", isCollectingRControllerData, isCollectingLControllerData));
            if (isCollectingRControllerData || isCollectingLControllerData) {
                Vector3 av, pos;
                Quaternion rot;
                bool angularVelocityAvailable = args.state.sourcePose.TryGetAngularVelocity(out av);
                bool rotationAvailable = args.state.sourcePose.TryGetRotation(out rot);
                bool positionAvailable = args.state.sourcePose.TryGetPosition(out pos);
                if (angularVelocityAvailable && rotationAvailable && positionAvailable) {

                    Quaternion invRot = Quaternion.Inverse(rot);
                    Vector3 transform = pos + (invRot * av);
                    //Debug.Log(string.Format("{3} - {4}, x: {0}, y: {1}, z: {2}",
                    //    av.x, av.y, av.z,
                    //    args.state.source.kind.ToString(),
                    //    args.state.source.handedness.ToString()));
                        
                    if(isCollectingRControllerData && args.state.source.handedness == InteractionSourceHandedness.Right) {
                        long timeElapsedMilliseconds = 0;
                        if (rCollectSamples.Count == 0) {
                            if (rStopWatch == null) {
                                rStopWatch = new System.Diagnostics.Stopwatch();
                            }
                            else {
                                rStopWatch.Reset();
                            }
                        }
                        else {
                            rStopWatch.Stop();
                            timeElapsedMilliseconds = rStopWatch.ElapsedMilliseconds;
                            rStopWatch.Start();
                            if(timeElapsedMilliseconds - rCollectSamples[rCollectSamples.Count-1].time < MinSampleInterval) {
                                // Sample rate too fast, ignore
                                return;
                            }
                        }
                        Sample sample = new Sample();
                        sample.time = timeElapsedMilliseconds;
                        sample.angularVelocity = transform;
                        rCollectSamples.Add(sample);
                    }
                    else if(isCollectingLControllerData && args.state.source.handedness == InteractionSourceHandedness.Left) {
                        long timeElapsedMilliseconds = 0;
                        if (lCollectSamples.Count == 0) {
                            if (lStopWatch == null) {
                                lStopWatch = new System.Diagnostics.Stopwatch();
                            } else {
                                lStopWatch.Reset();
                            }
                        } else {
                            lStopWatch.Stop();
                            timeElapsedMilliseconds = lStopWatch.ElapsedMilliseconds;
                            lStopWatch.Start();
                            if (timeElapsedMilliseconds - lCollectSamples[lCollectSamples.Count - 1].time < MinSampleInterval) {
                                // Sample rate too fast, ignore
                                return;
                            }
                        }
                        Sample sample = new Sample();
                        sample.time = timeElapsedMilliseconds;
                        sample.angularVelocity = transform;
                        lCollectSamples.Add(sample);
                    }
                }
            }  
        }

        void InteractionManager_SourceDetected(InteractionSourceDetectedEventArgs args) {

        }

        void triggerStart(InteractionSourceHandedness hand) {
            if (hand == InteractionSourceHandedness.Right) {
                rCollectSamples.Clear();
                isCollectingRControllerData = true;
                Debug.Log(string.Format("Right Hand Press"));
            } else if (hand == InteractionSourceHandedness.Left) {
                lCollectSamples.Clear();
                isCollectingLControllerData = true;
                Debug.Log(string.Format("Left Hand Press"));
            }
        }

        void InteractionManager_SourcePressed(InteractionSourcePressedEventArgs args) {
            if (rightHandStartTrigger != TriggerButton.NONE &&
                args.state.source.handedness == InteractionSourceHandedness.Right) {
                Debug.Log(string.Format("selectPressed:{0}, selectPressedAmount:{1}", args.state.selectPressed, args.state.selectPressedAmount));
                if (rightHandStartTrigger == TriggerButton.SELECT_BUTTON &&
                    (args.state.selectPressed || args.state.selectPressedAmount > 0.05)) {
                    // Select Button Pressed
                    triggerStart(args.state.source.handedness);
                }
                else if(rightHandStartTrigger == TriggerButton.GRASPE_BUTTON &&
                    (args.state.grasped)) {
                    // Hand Graspe Button Pressed
                    triggerStart(args.state.source.handedness);
                }
                else if (rightHandStartTrigger == TriggerButton.THUMB_STICK_BUTTON &&
                      (args.state.thumbstickPressed)) {
                    // Joystick Button Pressed
                    triggerStart(args.state.source.handedness);
                }
                else if (rightHandStartTrigger == TriggerButton.TOUCHPAD_BUTTON &&
                      (args.state.touchpadPressed)) {
                    // Touchpad Button Pressed
                    triggerStart(args.state.source.handedness);
                }
            }

            if (leftHandStartTrigger != TriggerButton.NONE &&
                 args.state.source.handedness == InteractionSourceHandedness.Left) {
                if (leftHandStartTrigger == TriggerButton.SELECT_BUTTON &&
                    (args.state.selectPressed || args.state.selectPressedAmount > 0.05)) {
                    // Select Button Pressed
                    triggerStart(args.state.source.handedness);
                } else if (leftHandStartTrigger == TriggerButton.GRASPE_BUTTON &&
                      (args.state.grasped)) {
                    // Hand Graspe Button Pressed
                    triggerStart(args.state.source.handedness);
                } else if (leftHandStartTrigger == TriggerButton.THUMB_STICK_BUTTON &&
                        (args.state.thumbstickPressed)) {
                    // Joystick Button Pressed
                    triggerStart(args.state.source.handedness);
                } else if (leftHandStartTrigger == TriggerButton.TOUCHPAD_BUTTON &&
                        (args.state.touchpadPressed)) {
                    // Touchpad Button Pressed
                    triggerStart(args.state.source.handedness);
                }
            }
        }

        void triggerEnd(InteractionSourceHandedness hand) {
            if (hand == InteractionSourceHandedness.Right && isCollectingRControllerData) {
                isCollectingRControllerData = false;
                Debug.Log(string.Format("Right Hand Release - sample count: " + rCollectSamples.Count));
#if WINDOWS_UWP
                Windows.System.Threading.ThreadPool.RunAsync(workItem => processCollectData(rCollectSamples));
#elif UNITY_EDITOR
                Thread t = new Thread(() => processCollectData(rCollectSamples));
                t.IsBackground = true;
                t.Start();
#endif
            } else if (hand == InteractionSourceHandedness.Left && isCollectingLControllerData) {
                isCollectingLControllerData = false;
                Debug.Log(string.Format("Left Hand Release - sample count: " + lCollectSamples.Count));
#if WINDOWS_UWP
                Windows.System.Threading.ThreadPool.RunAsync(workItem => processCollectData(lCollectSamples));
#elif UNITY_EDITOR
                Thread t = new Thread(() => processCollectData(lCollectSamples));
                t.IsBackground = true;
                t.Start();
#endif
            }
        }

        void InteractionManager_SourceReleased(InteractionSourceReleasedEventArgs args) {
            if (rightHandStartTrigger != TriggerButton.NONE &&
                args.state.source.handedness == InteractionSourceHandedness.Right) {
                if (rightHandStartTrigger == TriggerButton.SELECT_BUTTON &&
                    ( ( ! args.state.selectPressed) || args.state.selectPressedAmount > 0.05)) {
                    // Select Button Pressed
                    triggerEnd(args.state.source.handedness);
                } else if (rightHandStartTrigger == TriggerButton.GRASPE_BUTTON &&
                      ( ! args.state.grasped)) {
                    // Hand Graspe Button Pressed
                    triggerEnd(args.state.source.handedness);
                } else if (rightHandStartTrigger == TriggerButton.THUMB_STICK_BUTTON &&
                        ( ! args.state.thumbstickPressed)) {
                    // Joystick Button Pressed
                    triggerEnd(args.state.source.handedness);
                } else if (rightHandStartTrigger == TriggerButton.TOUCHPAD_BUTTON &&
                        ( ! args.state.touchpadPressed)) {
                    // Touchpad Button Pressed
                    triggerEnd(args.state.source.handedness);
                }
            }

            if (leftHandStartTrigger != TriggerButton.NONE &&
                 args.state.source.handedness == InteractionSourceHandedness.Left) {
                if (leftHandStartTrigger == TriggerButton.SELECT_BUTTON &&
                    ( ( ! args.state.selectPressed) || args.state.selectPressedAmount > 0.05)) {
                    // Select Button Pressed
                    triggerEnd(args.state.source.handedness);
                } else if (leftHandStartTrigger == TriggerButton.GRASPE_BUTTON &&
                      ( ! args.state.grasped)) {
                    // Hand Graspe Button Pressed
                    triggerEnd(args.state.source.handedness);
                } else if (leftHandStartTrigger == TriggerButton.THUMB_STICK_BUTTON &&
                        ( ! args.state.thumbstickPressed)) {
                    // Joystick Button Pressed
                    triggerEnd(args.state.source.handedness);
                } else if (leftHandStartTrigger == TriggerButton.TOUCHPAD_BUTTON &&
                        ( ! args.state.touchpadPressed)) {
                    // Touchpad Button Pressed
                    triggerEnd(args.state.source.handedness);
                }
            }
        }

        void OnDestroy () {
            // Release MS MR controller event
            InteractionManager.InteractionSourceUpdated -= InteractionManager_SourceUpdated;
            InteractionManager.InteractionSourceDetected -= InteractionManager_SourceDetected;
            InteractionManager.InteractionSourcePressed -= InteractionManager_SourcePressed;
            InteractionManager.InteractionSourceReleased -= InteractionManager_SourceReleased;

            isCollectingRControllerData = false;
            isCollectingLControllerData = false;

            sInstance = null;

            if (!mIsInitReady)
                return;


            SetSensorDataCallback (vrControllerHelper, null);
            _DataCallbackHolder = null;

            Shutdown (vrControllerHelper);

            vrControllerHelper = IntPtr.Zero;

        }

#if WINDOWS_UWP
        async void Load() {
            Windows.Storage.StorageFolder storageFolder =
                Windows.Storage.ApplicationData.Current.LocalFolder;
            Debug.Log("Storage Folder: " + storageFolder.Path);
            Windows.Storage.StorageFile sampleFile =
                await storageFolder.CreateFileAsync("trainData.dat",
                Windows.Storage.CreationCollisionOption.OpenIfExists);

            DataContractJsonSerializer serializer = 
                new DataContractJsonSerializer(typeof(TrainData));

            try {
                using (var reader = await sampleFile.OpenStreamForReadAsync())
                {
                    mTrainData = (TrainData)serializer.ReadObject(reader);
                }

                Dictionary<int, bool>.KeyCollection keyColl = mTrainData.useUserGesture.Keys;
                if (EnableDebugLog) Debug.Log ("[AirSigManager] Trained Data Loaded - " + string.Join (",", keyColl.Select (x => x.ToString ()).ToArray ()));

                long user = mTrainData.userGestureCount.Sum (x => x.Value);
                long common = mTrainData.commonGestureCount.Sum (x => x.Value);
                long fail = mTrainData.failedGestureCount.Sum (x => x.Value);
                long total = mTrainData.Total ();
                if (EnableDebugLog) Debug.Log (string.Format ("[AirSigManager] History stats:\n === User:{0}/{1}\n === Common:{2}/{3}\n === Fail:{4}/{5}",
                    user, total,
                    common, total,
                    fail, total));
            }
            catch(InvalidOperationException) {
                Debug.Log("Loading data failed");
            }
        }
#elif UNITY_EDITOR
        void Load () {
            if (File.Exists (Application.persistentDataPath + "/trainData.dat")) {
                BinaryFormatter bf = new BinaryFormatter ();
                FileStream file = File.Open (Application.persistentDataPath + "/trainData.dat", FileMode.Open);
                mTrainData = (TrainData) bf.Deserialize (file);
                file.Close ();

                Dictionary<int, bool>.KeyCollection keyColl = mTrainData.useUserGesture.Keys;
                if (EnableDebugLog) Debug.Log ("[AirSigManager] Trained Data Loaded - " + string.Join (",", keyColl.Select (x => x.ToString ()).ToArray ()));

                long user = mTrainData.userGestureCount.Sum (x => x.Value);
                long common = mTrainData.commonGestureCount.Sum (x => x.Value);
                long fail = mTrainData.failedGestureCount.Sum (x => x.Value);
                long total = mTrainData.Total ();
                if (EnableDebugLog) Debug.Log (string.Format ("[AirSigManager] History stats:\n === User:{0}/{1}\n === Common:{2}/{3}\n === Fail:{4}/{5}",
                    user, total,
                    common, total,
                    fail, total));

            }
        }
#endif

#if WINDOWS_UWP
        async void Save() {
            Windows.Storage.StorageFolder storageFolder =
            Windows.Storage.ApplicationData.Current.LocalFolder;
            Debug.Log("Storage Folder: " + storageFolder.Path);
            Windows.Storage.StorageFile sampleFile =
                await storageFolder.CreateFileAsync("trainData.dat",
                Windows.Storage.CreationCollisionOption.OpenIfExists);

            DataContractJsonSerializer serializer = 
                new DataContractJsonSerializer(typeof(TrainData));

            using (var writer = await sampleFile.OpenStreamForWriteAsync())
            {
                serializer.WriteObject(writer, mTrainData);
            }
        }
#elif UNITY_EDITOR
        void Save () {
            BinaryFormatter bf = new BinaryFormatter ();
            FileStream file;
            if (File.Exists (Application.persistentDataPath + "/trainData.dat")) {
                file = File.Open (Application.persistentDataPath + "/trainData.dat", FileMode.Open);
            } else {
                file = File.Create (Application.persistentDataPath + "/trainData.dat");
            }

            bf.Serialize (file, mTrainData);
            file.Close ();
        }
#endif

        private void printGestureStat() {
            foreach(KeyValuePair<int, ErrorCount> item in mCommonGestureStat.gestureStat) {
	            if(EnableDebugLog) Debug.Log("----------------------------");
	            if(EnableDebugLog) Debug.Log(String.Format("key:{0}  userErr:{1}  commErr:{2}", item.Key, item.Value.userErrCount, item.Value.commonErrCount));
            }
        }

        public IEnumerator UpdateDeveloperDefinedGestureStat(bool force) {
            bool isExist = false;
            if (!IsValidClassifier) {
                yield return null;
            }
            if (!mPredGestureStatDict.ContainsKey(FullClassifierPath)) {
                mPredGestureStatDict[FullClassifierPath] = new PredefinedSmartGestureStat();
            }
            isExist = mPredGestureStatDict[FullClassifierPath].isStatExist;

            if (isExist && !force) {
                yield return null;
            } else {
                foreach (KeyValuePair<string, List<float[]>> entry in mSmartTrainPredefinedCacheCollection) {
                    foreach (float[] sensorData in entry.Value) {
                        IdentifyPredefinedActionBundle bundleForPredefined = new IdentifyPredefinedActionBundle(0, entry.Key, sensorData);
                        //Dictionary<string, List<float[]>>.KeyCollection keyColl = mSmartTrainPredefinedCacheCollection.Keys;
                        //List<string> keys = keyColl.ToList();
                        Dictionary<string, bool>.KeyCollection keyColl = mTrainData.usePredefinedUserGesture.Keys;
                        List<string> keys = keyColl.ToList();
                        IdentifyPredefined(0, sensorData, keys, mClassifier, mSubClassifier,
                            (bundleArgu) => {
                                mPredGestureStatDict[FullClassifierPath].checkThenAdd(bundleArgu.matchGesture);
                                
                                if (bundleArgu.basedGesture != bundleArgu.matchGesture) {
                                    mPredGestureStatDict[FullClassifierPath].gestureStat[bundleArgu.matchGesture].commonErrCount++;
                                    if (EnableDebugLog) Debug.Log(string.Format("Try {0} but match {1} >>> commonErr + 1", bundleArgu.basedGesture, bundleArgu.matchGesture));
                                }
                                else {
                                    mPredGestureStatDict[FullClassifierPath].gestureConf[bundleArgu.matchGesture].commonConfidence += bundleArgu.conf;
                                }
                            }, bundleForPredefined,
                            false);

                        //Dictionary<int, bool>.KeyCollection keyColl = mTrainData.useUserGesture.Keys;
                        if (keyColl.Count > 0) {
                            //if(DEBUG_LOG_ENABLED) Debug.Log("[AirSigManager][UpdateGestureStat] try lookup for these keys: " + string.Join(",", keyColl.Select(x => x.ToString()).ToArray()));
                            // Verify first before we set to use "user gesture"
                            string[] againstIndex = new string[keyColl.Count];
                            keyColl.CopyTo(againstIndex, 0);
                            //string result = IdentifyPlayerGesture(0, sensorData, keys);
                            CustomGestureResult<string> result = IdentifyCustomGesture(0, sensorData, keys);
                            
                            if (null != result && result.bestMatch.Length > 0) {
                                mPredGestureStatDict[FullClassifierPath].checkThenAdd(result.bestMatch);
                                if (result.bestMatch != entry.Key) {
                                    mPredGestureStatDict[FullClassifierPath].gestureStat[result.bestMatch].userErrCount++;
                                    if (EnableDebugLog) Debug.Log(string.Format("Try {0} but match {1} >>> userErr + 1", entry.Key, result));
                                }
                                else {
                                    mPredGestureStatDict[FullClassifierPath].gestureConf[result.bestMatch].userConfidence += result.confidence;
                                }
                            }
                        }
                    }
                }
                if (EnableDebugLog) {
                    /*
                    string[] keys = mPredefinedGestureStat.Keys.ToArray();
                    Debug.Log(string.Format("key count: {0}", keys.Length));
                    foreach (string key in keys) {
                        Debug.Log(string.Format("key: {0}, userErr: {1}, commErr: {2}",
                            key, mPredefinedGestureStat[key].userErrCount, mPredefinedGestureStat[key].commonErrCount));
                    }
                    */
                }
                mPredGestureStatDict[FullClassifierPath].isStatExist = true;
                Save();
                yield return null;
            }
        }

        /// thread for Obtaining Oculus controller data
        private bool isCollectingRControllerData, isCollectingLControllerData;
        private struct Sample {
            public long time;
            public Vector3 angularVelocity;
        }
        private List<Sample> rCollectSamples = new List<Sample>();
        private List<Sample> lCollectSamples = new List<Sample>();


        private void processCollectData(List<Sample> data) {
            if (data.Count <= 0) return;
            /*
            using (System.IO.StreamWriter file =
            new System.IO.StreamWriter(@"C:\temp\sensor_data_mr.txt")) {
                foreach (Sample s in data) {
                    //// If the line doesn't contain the word 'Second', write the line to the file.
                    file.WriteLine(string.Format("{0},{1},{2},{3}", s.time, s.angularVelocity.x, s.angularVelocity.y, s.angularVelocity.z));
                }
            }
            */

            float[] transformData = new float[data.Count * 10];
            for(int i = 0; i < data.Count; i ++) {
                transformData[i * 10] = data[i].time;
                transformData[i * 10 + 1] = data[i].angularVelocity.x;
                transformData[i * 10 + 2] = data[i].angularVelocity.y;
                transformData[i * 10 + 3] = data[i].angularVelocity.z;
            }
            int size = Marshal.SizeOf(transformData[0]) * transformData.Length;
            IntPtr pnt = Marshal.AllocHGlobal(size);
            try {
                // Copy the array to unmanaged memory.
                Marshal.Copy(transformData, 0, pnt, transformData.Length);
                // Pass to the callback
                onSensorDataRecorded(pnt, data.Count, 10);
            } finally {
                // Free the unmanaged memory.
                Marshal.FreeHGlobal(pnt);
            }
        }

        /// Set the identification mode for the next incoming gesture
        public void SetMode(Mode mode) {
            if (mCurrentMode == mode) {
                if (EnableDebugLog) Debug.Log("[AirSigManager][SetMode] New mode (" + mode + ") equals to the existing mode so nothing will change...");
                return;
            }
            if (EnableDebugLog) Debug.Log("[AirSigManager][SetMode] New mode (" + mode + ")");
            if ((mCurrentMode & Mode.SmartTrainDeveloperDefined) > 0 && (mode & Mode.SmartTrainDeveloperDefined) == 0 && mCurrentPredefined.Count > 0) {
                // current mode contain smart train predefined and the new mode doesn't, trigger the smart train process
                if (EnableDebugLog) Debug.Log("[AirSigManager][SetMode] Changing mode and trigger SmartTrainDeveloperDefined process ...");
                SmartTrainPredefinedGesture(mCurrentPredefined.First());
            }
            mCurrentMode = mode;

            mTrainFailCount = 0;

           // clear all training
            mTrainingProgressGestures.Clear();
        }

        /// Set the identification target for the next incoming gesture
        public void SetTarget(List<int> target) {
            if (mCurrentTarget.SequenceEqual(target)) {
                if (EnableDebugLog) Debug.Log("[AirSigManager][SetTarget] New targets equal to the existing targets so nothing will change...");
                return;
            }
            
            mCurrentTarget = target;

            if (EnableDebugLog) Debug.Log("[AirSigManager][SetTarget] Targets: " + string.Join(",", mCurrentTarget.Select(x => x.ToString()).ToArray()));

            mTrainFailCount = 0;

            // clear all incomplete training
            mTrainingProgressGestures.Clear();
        }

        public void SetDeveloperDefinedTarget(List<string> target) {
            string gesture = null;
            if (mCurrentPredefined.Count > 0) {
                gesture = mCurrentPredefined.First(); // This gesture is recorded as before changed (for smart train to exec)
            }
            mCurrentPredefined = target==null?new List<string>():target;
            if (EnableDebugLog) Debug.Log("[AirSigManager][SetDeveloperDefinedTarget] Targets: " + string.Join(",", mCurrentPredefined.Select(x => x.ToString()).ToArray()));

            if ((mCurrentMode & Mode.SmartTrainDeveloperDefined) > 0 && null != gesture) {
                // current mode contain smart train predefined and the new mode doesn't, trigger the smart train process
                SmartTrainPredefinedGesture(gesture);
            }
        }

        public void SetClassifier(string classifier, string subClassifier) {
            mClassifier = classifier;
            mSubClassifier = subClassifier;
        }

        /// Reset smart training data
        public void ResetSmartTrain() {
            mTrainData.useUserGesture.Clear();
            mTrainData.usePredefinedUserGesture.Clear();
        }

        public void GetCustomGestureCache() {

        }

        /// Set custom gesture to engine
        public Dictionary<int, int> SetPlayerGesture(List<int> targets) {
            return SetPlayerGesture(targets, true);
        }

        public Dictionary<int, int> SetPlayerGesture(List<int> targets, bool clearOnSet) {
            Dictionary<int, int> result = new Dictionary<int, int>();
            foreach (int index in targets) {
                if ( ! mCustomGestureCache.ContainsKey(index)) {
                    continue;
                }
                if (EnableDebugLog) Debug.Log("gesture count - " + index + ": " + mCustomGestureCache[index].Count);
                List<float[]> cache = mCustomGestureCache[index];
                if(cache.Count() == 0) {
                    continue;
                }
                int totalLength = 0;
                int[] numDataEntryList = new int[cache.Count()];
                int[] dataEntryLengthList = new int[cache.Count()];
                for (int i = 0; i < cache.Count(); i++) {
                    totalLength += cache[i].Length;
                    numDataEntryList[i] = cache[i].Length / 10;
                    dataEntryLengthList[i] = 10;
                }
                float[] dataList = new float[totalLength];
                for (int i = 0, k = 0; i < cache.Count(); i++) {
                    float[] entry = cache[i];
                    for (int j = 0; j < entry.Length; j++) {
                        dataList[k] = entry[j];
                        k++;
                    }
                }
                result.Add(index, cache.Count());
                SetCustomGesture(vrControllerHelper, index, dataList, cache.Count(), numDataEntryList, dataEntryLengthList);
                if(clearOnSet) {
                    mCustomGestureCache[index].Clear();
                }
            }
            return result;
        }

        private void SetCustomGesture(string target, float[] data, int[] numDataEntry, int[] dataEntryLength) {
            byte[] targetInByte = Encoding.ASCII.GetBytes(target);
            SetCustomGestureStr(vrControllerHelper, targetInByte, target.Length, data, data.Length, numDataEntry, dataEntryLength);
        }

    }

    [Serializable]
    class TrainData {
        public Dictionary<int, float> trainProgress = new Dictionary<int, float> ();
        public Dictionary<int, bool> useUserGesture = new Dictionary<int, bool> (); // builtin common gesture settings for predefined gesture
        public Dictionary<string, bool> usePredefinedUserGesture = new Dictionary<string, bool>(); // smart gesture settings for predefined gesture
        // common gesture and user gesture statistic
        public Dictionary<int, long> userGestureCount = new Dictionary<int, long> ();
        public Dictionary<int, long> commonGestureCount = new Dictionary<int, long> ();
        public Dictionary<int, long> failedGestureCount = new Dictionary<int, long> ();

        public long IncUserGestureCount (int target) {
            long newValue;
            if (userGestureCount.ContainsKey (target)) {
                newValue = userGestureCount[target] + 1;
                userGestureCount[target] = newValue;
            } else {
                newValue = userGestureCount[target] = 1;
            }
            return newValue;
        }

        public long IncCommonGestureCount (int target) {
            long newValue;
            if (commonGestureCount.ContainsKey (target)) {
                newValue = commonGestureCount[target] + 1;
                commonGestureCount[target] = newValue;
            } else {
                newValue = commonGestureCount[target] = 1;
            }
            return newValue;
        }

        public long IncFailedGestureCount (int target) {
            long newValue;
            if (failedGestureCount.ContainsKey (target)) {
                newValue = failedGestureCount[target] + 1;
                failedGestureCount[target] = newValue;
            } else {
                newValue = failedGestureCount[target] = 1;
            }
            return newValue;
        }

        public long Total () {
            long userGestureTotal = userGestureCount.Sum (x => x.Value);
            long commonGestureTotal = commonGestureCount.Sum (x => x.Value);
            long failedGestureTotal = failedGestureCount.Sum (x => x.Value);
            return userGestureTotal + commonGestureTotal + failedGestureTotal;
        }
    }
}