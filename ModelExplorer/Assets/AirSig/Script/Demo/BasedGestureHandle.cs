using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.WSA.Input;

using AirSig;

public class BasedGestureHandle : MonoBehaviour {

    // Reference to AirSigManager for setting operation mode and registering listener
    public AirSigManager airsigManager;

    // Reference to the vive right hand controller for handing key pressing
    private GameObject controller;
    public ParticleSystem track;

    // UI for displaying current status and operation results 
    public Text textMode;
    public Text textResult;
    public GameObject instruction;
    public GameObject cHeartDown;

    protected string textToUpdate;

    protected readonly string DEFAULT_INSTRUCTION_TEXT = "Pressing trigger and write in the air\nReleasing trigger when finish";
    protected string defaultResultText;

    // Set by the callback function to run this action in the next UI call
    protected Action nextUiAction;
    protected IEnumerator uiFeedback;

    protected string GetDefaultIntructionText() {
        return DEFAULT_INSTRUCTION_TEXT;
    }

    protected void ToggleGestureImage(string target) {
        if ("All".Equals(target)) {
            cHeartDown.SetActive(true);
            foreach (Transform child in cHeartDown.transform) {
                child.gameObject.SetActive(true);
            }
        } else if ("Heart".Equals(target)) {
            cHeartDown.SetActive(true);
            foreach (Transform child in cHeartDown.transform) {
                if (child.name == "Heart") {
                    child.gameObject.SetActive(true);
                } else {
                    child.gameObject.SetActive(false);
                }
            }
        } else if ("C".Equals(target)) {
            cHeartDown.SetActive(true);
            foreach (Transform child in cHeartDown.transform) {
                if (child.name == "C") {
                    child.gameObject.SetActive(true);
                } else {
                    child.gameObject.SetActive(false);
                }
            }
        } else if ("Down".Equals(target)) {
            cHeartDown.SetActive(true);
            foreach (Transform child in cHeartDown.transform) {
                if (child.name == "Down") {
                    child.gameObject.SetActive(true);
                } else {
                    child.gameObject.SetActive(false);
                }
            }
        } else {
            cHeartDown.SetActive(false);
        }
    }

    protected IEnumerator setResultTextForSeconds(string text, float seconds, string defaultText = "") {
        string temp = textResult.text;
        textResult.text = text;
        yield return new WaitForSeconds(seconds);
        textResult.text = defaultText;
    }

    protected void checkDbExist() {
        bool isDbExist = airsigManager.IsDbExist;
        if (!isDbExist) {
            textResult.text = "<color=red>Cannot find DB files!\nMake sure\n'Assets/AirSig/StreamingAssets'\nis copied to\n'Assets/StreamingAssets'</color>";
            textMode.text = "";
            instruction.SetActive(false);
            cHeartDown.SetActive(false);
        }
    }

    Vector3 forward = new Vector3(0f, 0f, 0.1f);
    protected void UpdateUIandHandleControl() {
        if (Input.GetKeyUp(KeyCode.Escape)) {
            Application.Quit();
        }
        if (null != textToUpdate) {
            if(uiFeedback != null) StopCoroutine(uiFeedback);
            uiFeedback = setResultTextForSeconds(textToUpdate, 5.0f, defaultResultText);
            StartCoroutine(uiFeedback);
            textToUpdate = null;
        }

        if (controller == null) {
            controller = GameObject.Find("/MixedRealityCameraParent/MotionControllers/RightController");
        }
        else {
            track.transform.rotation = controller.transform.rotation;
            track.transform.position = controller.transform.position;
            track.transform.Translate(forward);
        }

        if (nextUiAction != null) {
            nextUiAction();
            nextUiAction = null;
        }
    }

    protected virtual void Awake() {
        InteractionManager.InteractionSourcePressed += InteractionManager_InteractionSourcePressed;
        InteractionManager.InteractionSourceReleased += InteractionManager_InteractionSourceReleased;
    }

    protected virtual void OnDestroy() {
        // Release MS MR controller event
        InteractionManager.InteractionSourcePressed -= InteractionManager_InteractionSourcePressed;
        InteractionManager.InteractionSourceReleased -= InteractionManager_InteractionSourceReleased;
    }

    protected virtual void OnResetKeyPressed() {

    }

    bool isSetBackgroundSuccess = false;
    protected virtual void Update() {
        if (!isSetBackgroundSuccess) {
            GameObject[] camObjs = GameObject.FindGameObjectsWithTag("MainCamera");
            foreach (GameObject camObj in camObjs) {
                Camera cam = camObj.GetComponent<Camera>();
                if (cam.clearFlags != CameraClearFlags.SolidColor) {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    isSetBackgroundSuccess = true;
                }
            }
        }
    }

    private void InteractionManager_InteractionSourceReleased(InteractionSourceReleasedEventArgs args) {
        Debug.Log("BaseGestureHandle: " + args.state.ToString());
        if (args.state.selectPressed || args.state.selectPressedAmount > 0.05) {
            if (args.state.source.handedness == InteractionSourceHandedness.Right) {
                track.Stop();
                Debug.Log("BaseGestureHandle: Release");
            }
        }
    }

    private void InteractionManager_InteractionSourcePressed(InteractionSourcePressedEventArgs args) {
        Debug.Log("BaseGestureHandle: " + args.state.ToString());
        if (args.state.selectPressed || args.state.selectPressedAmount > 0.05) {
            if (args.state.source.handedness == InteractionSourceHandedness.Right) {
                track.Stop();
                track.Play();
                Debug.Log("BaseGestureHandle: Play");
            }
        } else if (args.state.touchpadPressed) {
            OnResetKeyPressed();
        }
    }
}
