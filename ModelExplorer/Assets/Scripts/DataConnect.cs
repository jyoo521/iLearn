using UnityEngine;
//using Pathfinding.Serialization.JsonFx; //old method
using Newtonsoft.Json;
using System.Collections;
using UnityEngine.Networking;

public class DataConnect : MonoBehaviour
{
    public GameObject myPrefab;
    //string WebsiteURL = "https://dwhi045.azurewebsites.net/tables/Sheep?zumo-api-version=2.0.0";

    string WebsiteURL = "https://jyoo521.azurewebsites.net/tables/Sheep?zumo-api-version=2.0.0";

    string jsonResponse;

    void Start()
    {
        //Reguest.GET can be called passing in your ODATA url as a string in the form:
        //http://{Your Site Name}.azurewebsites.net/tables/{Your Table Name}?zumo-api-version=2.0.0
        //The response produce is a JSON string
        //old code string jsonResponse = Request.GET(_WebsiteURL);
        
        WWW myWww = new WWW(WebsiteURL);
        while (myWww.isDone == false) ;

        jsonResponse = myWww.text;
        //StartCoroutine(GetData());
        //Just in case something went wrong with the request we check the reponse and exit if there is no response.
        if (string.IsNullOrEmpty(jsonResponse))
        {
            return;
        }

        //We can now deserialize into an array of objects - in this case the class we created. The deserializer is smart enough to instantiate all the classes and populate the variables based on column name.
        //Sheep[] Sheeps = JsonReader.Deserialize<Sheep[]>(jsonResponse);
        Sheep[] Sheeps = JsonConvert.DeserializeObject<Sheep[]>(jsonResponse);


        //----------------------
        //YOU WILL NEED TO DECLARE SOME VARIABLES HERE SIMILAR TO THE CREATIVE CODING TUTORIAL

        int i = 0;
        //int totalObjects = 30;
        //float totalDistance = 2.9f;
        //----------------------

        //We can now loop through the array of objects and access each object individually
        foreach (Sheep Sheep in Sheeps)
        {
            //Example of how to use the object
            Debug.Log("This Sheeps name is: " + Sheep.SheepName);
            //----------------------
            //YOUR CODE TO INSTANTIATE NEW PREFABS GOES HERE
            //float perc = i / (float)totalObjects;
            //float sin = Mathf.Sin(perc * Mathf.PI / 2);

            //float x = 1 + i * .5f;
            //float y = 0.6f;
            //float z = 2.0f;
            float x = Sheep.X;
            float y = Sheep.Y;
            float z = Sheep.Z;

            var newObject = (GameObject)Instantiate(myPrefab, new Vector3(x, y, z), Quaternion.identity);
            newObject.transform.Rotate(0, 180, 0);
            //newObject.GetComponent<CubeScript>().SetSize(.45f * (1.0f - perc));
            //newObject.GetComponent<CubeScript>().rotateSpeed = .2f + perc * 4.0f;
            //newObject.transform.Find("New Text").GetComponent<TextMesh>().text = Sheep.SheepName;//"Hullo Again";
            i++;

            //----------------------
        }
    }

    IEnumerator GetData()
    {
        Debug.Log("Getting  Data");
        UnityWebRequest www = UnityWebRequest.Get(WebsiteURL);       
        www.SendWebRequest();
        //yield return www.SendWebRequest();
        {
            Debug.Log("Retrieved  Data For " + WebsiteURL);
            new WaitForSeconds(40);
            
            jsonResponse = www.downloadHandler.text;
            yield return new WaitForSeconds(1);
            //yield return new WaitForSeconds(20);
        }
    }

    // Update is called once per frame
    void Update()
    {

    }

}
