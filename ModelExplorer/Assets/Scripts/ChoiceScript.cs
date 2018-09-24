using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class ChoiceScript : MonoBehaviour {

    public GameObject TextBox;
    public GameObject Choice01;
    public GameObject Choice02;
    public GameObject Choice03;
    public GameObject Choice04;
    public int ChoiceMade;

    public void ChoiceOption1 ()
    {
        TextBox.GetComponent<Text>().text = "Sorry, try again";
        ChoiceMade = 1;
    }
    public void ChoiceOption2()
    {
        TextBox.GetComponent<Text>().text = "Sorry, try again";
        ChoiceMade = 2;
    }
    public void ChoiceOption3()
    {
        TextBox.GetComponent<Text>().text = "Sorry, try again";
        ChoiceMade = 3;
    }
    public void ChoiceOption4()
    {
        TextBox.GetComponent<Text>().text = "Correct, good job!";
        ChoiceMade = 4;
    }

    // Update is called once per frame
    void Update () {
		
	}
}
