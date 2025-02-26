using System;
using System.Text;
using JetBrains.Annotations;
using Unity.FilmInternalUtilities;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Serialization;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Unity.MeshSync
{

[RequireComponent(typeof(Animator))]
[ExecuteInEditMode]
internal class SceneCachePlayer : MeshSyncPlayer {
    #region Types
    public enum TimeUnit {
        Seconds,
        Frames,
    }

    public enum BaseFrame {
        Zero = 0,
        One = 1,
    }
    
    #endregion


//----------------------------------------------------------------------------------------------------------------------
    
    SceneCachePlayer() : base() {
        SetSaveAssetsInScene(false);
        MarkMeshesDynamic(true);
    }

    protected override void InitInternalV() {
        
    }
    
    
//----------------------------------------------------------------------------------------------------------------------

    internal string GetSceneCacheFilePath() { return m_sceneCacheFilePath; }
    internal bool IsSceneCacheOpened() { return m_sceneCache;}

    internal void SetAutoplay(bool autoPlay) {
        //[Note-sin: 2021-1-18] May be called before m_animator is initialized in Playmode.
        //It is expected that the animator was already disabled previously in EditMode though.
        if (null == m_animator)
            return;
        
        m_animator.enabled = autoPlay;
    }

    internal float GetRequestedNormalizedTime() { return m_reqNormalizedTime;}

    internal TimeUnit GetTimeUnit() { return m_timeUnit; }

    internal void SetTimeUnit(TimeUnit timeUnit) {
        m_timeUnit = timeUnit;
        if (m_timeUnit == TimeUnit.Frames)
            m_interpolation = false;        
    }


    internal float GetTime() { return m_time;}
    internal void SetTime(float time) { m_time = time; }

    internal bool GetInterpolation() { return m_interpolation; }
    internal void SetInterpolation(bool interpolation) { m_interpolation = interpolation;}

    internal BaseFrame GetBaseFrame() { return m_baseFrame; }
    internal void SetBaseFrame(BaseFrame baseFrame) { m_baseFrame = baseFrame; }

    internal int GetFrame() { return m_frame; }
    internal void SetFrame(int frame) { m_frame = frame;}

    internal int GetPreloadLength() { return m_preloadLength;}
    internal void SetPreloadLength(int preloadLength) { m_preloadLength = preloadLength;}

    
//----------------------------------------------------------------------------------------------------------------------
    
    internal void RequestNormalizedTime(float normalizedTime) {
        m_reqNormalizedTime = normalizedTime;
        float time = normalizedTime * m_timeRange.end;
        
        switch (m_timeUnit) {
            case TimeUnit.Seconds: {
                m_time = time;
                ClampTime();
                break;
            }
            case TimeUnit.Frames: {
                m_frame = m_sceneCache.GetFrame(time);                
                break;
            }
            default: break;
        }
       
        
    }

    [CanBeNull]
    internal AnimationCurve GetTimeCurve() {
        if (!IsSceneCacheOpened())
            return null;
        
        return m_sceneCache.GetTimeCurve(InterpolationMode.Constant);
    }

    internal TimeRange GetTimeRange() { return m_timeRange;}
    
    

//----------------------------------------------------------------------------------------------------------------------
    #region Properties
    public int frameCount {
        get { return m_sceneCache.sceneCount; }
    }

#if UNITY_EDITOR
    public bool foldCacheSettings {
        get { return m_foldCacheSettings; }
        set { m_foldCacheSettings = value; }
    }
    public string dbgProfileReport {
        get { return m_dbgProfileReport; }
    }
#endif
    #endregion

    #region Internal Methods

#if UNITY_EDITOR    
    internal bool OpenCacheInEditor(string path) {

        if (!OpenCacheInternal(path)) {
            return false;
        }

        //Delete all children
        if (gameObject.IsPrefabInstance()) {
            PrefabUtility.UnpackPrefabInstance(gameObject, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);            
        } 
        gameObject.DestroyChildrenImmediate();
         
        //Initialization after opening a cache file
        m_sceneCacheFilePath = System.IO.Path.GetFullPath(path).Replace('\\','/');
               
        UpdatePlayer(/* updateNonMaterialAssets = */ true);
        ExportMaterials(false, true);
        ResetTimeAnimation();
        
        SceneData scene = GetLastScene();
        if (!scene.submeshesHaveUniqueMaterial) {
            m_config.SyncMaterialList = false;
        }
        
        return true;
    }
    

    private SceneData GetLastScene() {
        if (m_sceneCache)
            return m_sceneCache.GetSceneByTime(m_timePrev, m_interpolation);
        return default(SceneData);
    }
    
#endif //UNITY_EDITOR    

//----------------------------------------------------------------------------------------------------------------------    

    private bool OpenCacheInternal(string path) {
        CloseCache();

        m_sceneCache = SceneCacheData.Open(path);
        if (!m_sceneCache) {
            Debug.LogError($"SceneCachePlayer: cache open failed ({path})");
            return false;            
        }
        
        m_timeRange = m_sceneCache.timeRange;
        
#if UNITY_EDITOR
        SetSortEntities(true);
#endif
        LogDebug($"SceneCachePlayer: cache opened ({path})");

        //[Note-sin: 2021-7-19] Time/Frame 0 must be loaded first, because the data of other frames might contain "No change from frame 0" 
        LoadSceneCacheToScene(0, m_interpolation);
        
        return true;
    }
    
    internal void CloseCache() {
        if (m_sceneCache) {
            m_sceneCache.Close();
            LogDebug($"SceneCachePlayer: cache closed ({m_sceneCacheFilePath})");
        }
        m_timePrev = -1;
    }

    
//----------------------------------------------------------------------------------------------------------------------
    
#if UNITY_EDITOR
    internal bool ResetTimeAnimation() {
        if (m_sceneCache.sceneCount < 2)
            return false;

        AnimationClip clip = null;
        if (m_animator.runtimeAnimatorController != null) {
            AnimationClip[] clips = m_animator.runtimeAnimatorController.animationClips;
            if (clips != null && clips.Length > 0) {
                AnimationClip tmp = m_animator.runtimeAnimatorController.animationClips[0];
                if (tmp != null) {
                    clip = tmp;
                    Undo.RegisterCompleteObjectUndo(clip, "SceneCachePlayer");
                }
            }
        }

        if (null == clip) {
            clip = new AnimationClip();

            string assetsFolder = GetAssetsFolder();

            string animPath       = string.Format("{0}/{1}.anim", assetsFolder, gameObject.name);
            string controllerPath = string.Format("{0}/{1}.controller", assetsFolder, gameObject.name);
            clip = Misc.SaveAsset(clip, animPath);
            if (clip.IsNullRef()) {
                Debug.LogError("[MeshSync] Internal error in initializing clip for SceneCache");
                return false;
                
            }

            m_animator.runtimeAnimatorController = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPathWithClip(controllerPath, clip);
        }
        float sampleRate = m_sceneCache.sampleRate;
        if (sampleRate > 0.0f)
            clip.frameRate = sampleRate;

        Type tPlayer = typeof(SceneCachePlayer);
        clip.SetCurve("", tPlayer, "m_time", null);
        clip.SetCurve("", tPlayer, "m_frame", null);
        if (m_timeUnit == TimeUnit.Seconds) {
            AnimationCurve curve = m_sceneCache.GetTimeCurve(InterpolationMode.Constant);
            clip.SetCurve("", tPlayer, "m_time", curve);
        } else if (m_timeUnit == TimeUnit.Frames) {
            AnimationCurve curve = m_sceneCache.GetFrameCurve((int)m_baseFrame);
            clip.SetCurve("", tPlayer, "m_frame", curve);
        }
        

        AssetDatabase.SaveAssets();
        UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        return true;
    }
#endif

    private void UpdatePlayer(bool updateNonMaterialAssets) {


        if (m_timeUnit == TimeUnit.Frames) {
            int offset = (int)m_baseFrame;
            m_frame = Mathf.Clamp(m_frame, offset, frameCount + offset);
            m_time = m_sceneCache.GetTime(m_frame - offset);
        }

        if (!m_sceneCache) {
            return;
        }
        
        if (m_time != m_timePrev) {
            LoadSceneCacheToScene(m_time, updateNonMaterialAssets);
        } else if(m_sceneCache.preloadLength != m_preloadLength) {
            m_sceneCache.preloadLength = m_preloadLength;
            m_sceneCache.Preload(m_sceneCache.GetFrame(m_time));
        }

    }
    #endregion


    void LoadSceneCacheToScene(float time, bool updateNonMaterialAssets) {
        m_timePrev = m_time = time;
        m_sceneCache.preloadLength = m_preloadLength;
#if UNITY_EDITOR
        ulong sceneGetBegin = Misc.GetTimeNS();
#endif
        // get scene
        SceneData scene = m_sceneCache.GetSceneByTime(m_time, m_interpolation);
#if UNITY_EDITOR
        m_dbgSceneGetTime = Misc.NS2MS(Misc.GetTimeNS() - sceneGetBegin);
#endif

        if (scene) {
#if UNITY_EDITOR
            ulong sceneUpdateBegin = Misc.GetTimeNS();
#endif
            // update scene
            this.BeforeUpdateScene();
            this.UpdateScene(scene, updateNonMaterialAssets);
            this.AfterUpdateScene();
#if UNITY_EDITOR
            SetSortEntities(false);

            if (m_config.Profiling) {
                m_dbgSceneUpdateTime = Misc.NS2MS(Misc.GetTimeNS() - sceneUpdateBegin);
                UpdateProfileReport(scene);
            }
#endif
        }
        
    }
    
//----------------------------------------------------------------------------------------------------------------------

    protected override void OnBeforeSerializeMeshSyncPlayerV() {
        
    }

    protected override void OnAfterDeserializeMeshSyncPlayerV() {
        
        
        if (m_version < (int) SceneCachePlayerVersion.STRING_PATH_0_4_0) {
            Assert.IsNotNull(m_cacheFilePath);           
            m_sceneCacheFilePath = m_cacheFilePath.GetFullPath();
        } 
        
        m_version = CUR_SCENE_CACHE_PLAYER_VERSION;
    }   
    
//----------------------------------------------------------------------------------------------------------------------
    

#if UNITY_EDITOR
    void UpdateProfileReport(SceneData data) {
        StringBuilder sb = new System.Text.StringBuilder();
        SceneProfileData prof = data.profileData;
        sb.AppendFormat("Scene Get: {0:#.##}ms\n", m_dbgSceneGetTime);
        sb.AppendFormat("Scene Update: {0:#.##}ms\n", m_dbgSceneUpdateTime);
        sb.AppendFormat("\n");

        {
            ulong sizeEncoded = prof.sizeEncoded;
            if (sizeEncoded > 1000000)
                sb.AppendFormat("Cache: {0:#.##}MB encoded, {1:#.##}MB decoded, ", sizeEncoded / 1000000.0, prof.sizeDecoded / 1000000.0);
            else if (sizeEncoded > 1000)
                sb.AppendFormat("Cache: {0:#.##}KB encoded, {1:#.##}KB decoded, ", sizeEncoded / 1000.0, prof.sizeDecoded / 1000.0);
            else
                sb.AppendFormat("Cache: {0}B encoded, {1}B decoded, ", sizeEncoded, prof.sizeDecoded);
            sb.AppendFormat("{0} verts\n", prof.vertexCount);
        }
        sb.AppendFormat("Cache Load: {0:#.##}ms\n", prof.loadTime);
        double MBperSec = ((double)prof.sizeEncoded / 1000000.0) / (prof.readTime / 1000.0);
        sb.AppendFormat("  Cache Read: {0:#.##}ms ({1:#.##}MB/sec)\n", prof.readTime, MBperSec);
        sb.AppendFormat("  Cache Decode: {0:#.##}ms (total of worker threads)\n", prof.decodeTime);
        if (prof.setupTime > 0)
            sb.AppendFormat("Setup Scene: {0:#.##}ms\n", prof.setupTime);
        if (prof.lerpTime > 0)
            sb.AppendFormat("Interpolate Scene: {0:#.##}ms\n", prof.lerpTime);
        m_dbgProfileReport = sb.ToString();
    }

    
#endif
    
//----------------------------------------------------------------------------------------------------------------------

    void LogDebug(string logMessage) {
        if (!m_config.Logging)
            return;

        Debug.Log(logMessage); 
    }
    
//----------------------------------------------------------------------------------------------------------------------
    
    void ClampTime() {
        m_time = Mathf.Clamp(m_time, m_timeRange.start, m_timeRange.end);
    }
    
//----------------------------------------------------------------------------------------------------------------------
    
    #region Events
#if UNITY_EDITOR
    void Reset() {
        m_config = MeshSyncProjectSettings.GetOrCreateSettings().GetDefaultSceneCachePlayerConfig();            
    }

    void OnValidate() {
        if (!m_sceneCache)
            return;
        
        ClampTime();
    }
#endif

//----------------------------------------------------------------------------------------------------------------------    
    protected override void OnEnable() {

        
        base.OnEnable();
        m_animator = GetComponent<Animator>();
        if (!string.IsNullOrEmpty(m_sceneCacheFilePath)) {
            OpenCacheInternal(m_sceneCacheFilePath);
        }
        
        if (!m_sceneCache)
            return;
        
        ClampTime();
        
    }

    protected override void OnDisable() {
        base.OnDisable();
        CloseCache();
    }

    // note:
    // Update() is called *before* animation update.
    // in many cases m_time is controlled by animation system. so scene update must be handled in LateUpdate()
    void LateUpdate() {
        UpdatePlayer( updateNonMaterialAssets: false);
    }
    #endregion

//----------------------------------------------------------------------------------------------------------------------
    
    [SerializeField] string    m_sceneCacheFilePath = null; //The full path of the file. Use '/'
    [SerializeField] DataPath  m_cacheFilePath = null; //OBSOLETE
    [SerializeField] TimeUnit  m_timeUnit      = TimeUnit.Seconds;
    [SerializeField] float     m_time;
    [SerializeField] bool      m_interpolation = false;
    [SerializeField] BaseFrame m_baseFrame     = BaseFrame.One;
    [SerializeField] int       m_frame         = 1;
    [SerializeField] int       m_preloadLength = 1;

    
    [HideInInspector][SerializeField] private int m_version = (int) CUR_SCENE_CACHE_PLAYER_VERSION;
    private const int CUR_SCENE_CACHE_PLAYER_VERSION = (int) SceneCachePlayerVersion.STRING_PATH_0_4_0;
        
    SceneCacheData m_sceneCache;
    TimeRange      m_timeRange;
    float          m_timePrev = -1;
    Animator       m_animator = null;
    private float m_reqNormalizedTime = 0;

#if UNITY_EDITOR
    [SerializeField] bool m_foldCacheSettings = true;
    float                 m_dbgSceneGetTime;
    float                 m_dbgSceneUpdateTime;
    string                m_dbgProfileReport;
#endif

//----------------------------------------------------------------------------------------------------------------------    
    
    enum SceneCachePlayerVersion {
        NO_VERSIONING = 0, //Didn't have versioning in earlier versions
        STRING_PATH_0_4_0 = 2, //0.4.0-preview: the path is declared as a string 
    
    }
    
}

} //end namespace
