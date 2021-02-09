#define COMPILE_WITH_TESTS
#define ALLOW_1P_AS_2P

// Auth lobby: Each player is required to register into the game before it begins
#define USE_AUTH_LOBBY

// King lobby: Anyone can mess with anything, and the network owner of ht8b script object
//   is responsible for sending out updates
//#define USE_KING_LOBBY

using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;
using System;

public class ht8b_menu : UdonSharpBehaviour
{
    private const float COLOUR_TRANSITION = 0.1f;
    private const float COLOUR_TRANS_RECIP = 10.0f;

    // Networking msg id shite
    private const byte sevenBallJoin = 0x00;
    private const byte sevenBallLeave = 0x10;
    private const byte sevenBallNewJoinPlayer = 0x20;  // Special new-joiner event to catch up quicker

    private const byte sevenBallMenuLocation = 0x30;
    private const byte sevenBallCol = 0x40;
    private const byte sevenBallGameMode = 0x50;
    private const byte sevenBallTimeLimit = 0x60;
    private const byte sevenBallTeams = 0x70;

    // UI materials
    public Material high;
    public Material low;

    // UI element objects
    public GameObject mainLocation;

    public MeshRenderer[] gameModes;
    public MeshRenderer[] timeLimits;
    public MeshRenderer[] joinButtons;
    public MeshRenderer start;
    private BoxCollider startCollider;
    public GameObject newGame;
    public GameObject[] colourSelectors;
    public MeshRenderer[] teamButtons;
    public Text[] textPlayers;
    public BoxCollider[] lobbyOwnersOnly;

    // Networking stuff
    public GameObject[] tokens;
    public ht8b gameStateManager;

    // Visual
    public SkinnedMeshRenderer sandTimer;

    // Localize from ht8b.cs
    private Texture[] ballTextures;
    private GameObject[] ballRenderers;
    private Material ballMat;
    private Material tableMat;
    private GameObject scoreCard;

    // Networked
    [HideInInspector]
    public uint gameModeID;
    [HideInInspector]
    public uint colourID;
    [HideInInspector]
    public uint timerID;
    [HideInInspector]
    public uint teamsAllowed;

    // Non-critical
    private uint menuLocation; // 0: NewGame button, 1: Main menu

    [HideInInspector]
    public bool isGameRunning = false;

#if USE_AUTH_LOBBY
    private VRCPlayerApi[] playerAPIs = new VRCPlayerApi[4];
    private bool[] playersReadyStatus = new bool[4];

    private int localPlayerID = -1;      // -1: not joined, 0-3: joined as ID
#endif

    private Vector3[] resetPositions = new Vector3[16];

    public Color grayFabric = new Color(0.3f, 0.3f, 0.3f, 1.0f);
    public Color redFabric = new Color(0.8962264f, 0.2081864f, 0.1310519f);
    public Color blueFabric = new Color(0.1f, 0.6f, 1.0f, 1.0f);
    public Color whiteFabric = new Color(0.8f, 0.8f, 0.8f, 1.0f);

    public Color blackLightColour = new Color(0.01f, 0.01f, 0.01f, 1.0f);

    [HideInInspector] public Color tableSource;   // Cloth color
    private Color tableCurrent;
    [HideInInspector] public Color tableSourceLight;    // Light color
    private Color tableColourLight;

    // VFX stuff

    private float colourChangeTimer = 9.0f;

    private bool isTransitioned = false;
    private float sandTimerTargetWeight = 0.0f;
    private float sandTimerWeight = 0.0f;

    public bool isInGame = false;
    public bool isLobbyLeader = false;

    private uint maxColour = 2u;

    private float lastCheck = 0.0f;

    [HideInInspector]
    public int joinAsID = 0;

    // Change menu location button
    [HideInInspector]
    public uint inMenuLocation = 0;

    [HideInInspector]
    public int colourChangeDir = 0;

    [HideInInspector]
    public int allowTeams = 0;

    [HideInInspector]
    public int timeLimitID = 0;

    [HideInInspector]
    public int inputGameModeID = 0;

    public void Start()
    {
        startCollider = start.GetComponent<BoxCollider>();
        if (!startCollider)
        {
            Debug.LogError("ht8b_menu: Start: start object has no box collider - aborting");
            gameObject.SetActive(false);
        }

        // getting inspector variables from ht8b.cs
        ballTextures = gameStateManager.sets;
        ballRenderers = gameStateManager.ballsToRender;
        ballMat = gameStateManager.ballMaterial;
        tableMat = gameStateManager.tableMaterial;
        scoreCard = gameStateManager.scoreCardRenderer.gameObject;

        for (int i = 0; i < 16; i++)
        {
            resetPositions[i] = ballRenderers[i].transform.position;
        }

        // Initialize visual state to match internal
        ResetInternalState();
    }

    public void Update()
    {
#if USE_AUTH_LOBBY
        if (Time.timeSinceLevelLoad > lastCheck + 1.5f)
        {
            lastCheck = Time.timeSinceLevelLoad;
            RefreshLobby();
        }
#endif

        if (colourChangeTimer <= COLOUR_TRANSITION)
        {
            colourChangeTimer -= Time.deltaTime;

            if (colourChangeTimer < 0.0f)
            {
                if (!isTransitioned)
                {
                    ApexTransition();
                    isTransitioned = true;
                }
            }

            float scaling = Mathf.Abs(colourChangeTimer) * COLOUR_TRANS_RECIP;

            if (colourChangeTimer < -COLOUR_TRANSITION)
            {
                colourChangeTimer = 9.0f;
                scaling = 1.0f;
            }

            for (int i = 0; i < 16; i++)
            {
                ballRenderers[i].transform.localScale = new Vector3(scaling, scaling, scaling);
            }
        }

        if (menuLocation == 0)
        {
            tableColourLight = tableSourceLight * (Mathf.Sin(Time.timeSinceLevelLoad * 3.0f) * 0.5f + 1.0f);
        }
        else
        {
            tableColourLight = Color.Lerp(tableColourLight, tableSourceLight, Time.deltaTime * 5.0f);
            tableCurrent = Color.Lerp(tableCurrent, tableSource, Time.deltaTime * 5.0f);
            tableMat.SetColor("_ClothColour", tableCurrent);
        }

        tableMat.SetColor("_EmissionColour", new Color(tableColourLight.r, tableColourLight.g, tableColourLight.b, 0.0f));

        sandTimerWeight = Mathf.Lerp(sandTimerWeight, sandTimerTargetWeight, Time.deltaTime * 5.0f);
        sandTimer.SetBlendShapeWeight(0, sandTimerWeight);
    }

    public void ViewTimeLimit()
    {
        for (int i = 0; i < timeLimits.Length; i++)
        {
            if (i == timerID)
            {
                timeLimits[i].sharedMaterial = high;
            }
            else
            {
                timeLimits[i].sharedMaterial = low;
            }
        }

        // TODO: Sandtimer vars

        if (timerID == 0)
        {
            sandTimer.enabled = false;
        }
        else
        {
            sandTimer.enabled = true;

            if (timerID == 1)
            {
                sandTimerTargetWeight = 50.0f;
            }
            else
            {
                sandTimerTargetWeight = 0.0f;
            }
        }
    }

    public void ViewMenu()
    {
        if (menuLocation == 0)
        {
            newGame.SetActive(true);
            mainLocation.SetActive(false);
        }
        else
        {
            scoreCard.SetActive(false);
            newGame.SetActive(false);
            mainLocation.SetActive(true);

            //table_material.SetColor( "_EmissionColour", k_lightColour_black );
            tableSourceLight = blackLightColour;

            // Run view states when we load this menu
            ViewColours();
            ViewGameMode();
            ViewTimeLimit();

#if USE_AUTH_LOBBY
            ViewTeam();
            ViewPlayers();

            // Add controls for lobby master
            if (localPlayerID == 0)
            {
                for (int i = 0; i < lobbyOwnersOnly.Length; i++)
                {
                    lobbyOwnersOnly[i].enabled = true;
                }
            }
            else
            {
                for (int i = 0; i < lobbyOwnersOnly.Length; i++)
                {
                    lobbyOwnersOnly[i].enabled = false;
                }
            }

            for (int i = 0; i < 16; i++)
            {
                ballRenderers[i].transform.position = resetPositions[i];
            }
#endif
        }
    }

    // Reset to fully defined state
    public void ResetInternalState()
    {
        gameModeID = 0u;
        colourID = 0;
        timerID = 0;
        teamsAllowed = 0;
        menuLocation = 0;
        isGameRunning = false;

#if USE_AUTH_LOBBY
        localPlayerID = -1;

        for (int i = 0; i < 4; i++)
        {
            playersReadyStatus[i] = false;
            playerAPIs[i] = Networking.GetOwner(tokens[i]);
        }
#endif

        ViewMenu();
    }

#if USE_AUTH_LOBBY
    public void ViewTeam()
    {
        if (teamsAllowed == 0)
        {
            joinButtons[2].gameObject.SetActive(false);
            joinButtons[3].gameObject.SetActive(false);

            teamButtons[0].sharedMaterial = low;
            teamButtons[1].sharedMaterial = high;
        }
        else
        {
            joinButtons[2].gameObject.SetActive(true);
            joinButtons[3].gameObject.SetActive(true);

            teamButtons[0].sharedMaterial = high;
            teamButtons[1].sharedMaterial = low;
        }
    }
#endif


    private void ViewColours()
    {
        if (colourChangeTimer > COLOUR_TRANSITION)
        {
            colourChangeTimer = COLOUR_TRANSITION;
        }

        if (colourChangeTimer < 0.0f)
        {
            colourChangeTimer = -colourChangeTimer;
        }

        isTransitioned = false;
    }

    private void ViewGameMode()
    {
        for (uint i = 0; i < gameModes.Length; i++)
        {
            if (i == gameModeID)
            {
                gameModes[i].sharedMaterial = high;
            }
            else
            {
                gameModes[i].sharedMaterial = low;
            }
        }

        bool isViewColourSelectors = true;

        if (gameModeID == 1u) // 9 ball
        {
            tableSource = blueFabric;
            isViewColourSelectors = false;
        }
        else // 8 ball derivatives
        {
            tableSource = grayFabric;
        }

        if (gameModeID == 2u)
        {
            maxColour = 3u;
        }
        else
        {
            maxColour = 2u;
        }

        colourSelectors[0].SetActive(isViewColourSelectors);
        colourSelectors[1].SetActive(isViewColourSelectors);
    }



#if USE_AUTH_LOBBY // TODO: This is a massive ifdef. Figure out what USE_AUTH_LOBBY does and refactor it away.
    void ViewPlayers()
    {
        if (isInGame)
        {
            this.gameObject.SetActive(false);
            return;
        }

        // Take most updated data
        RefreshUsersAPI();

        // Constantly internalize local state into ht8b.cs to make sure we can allow us to play
        // when system control is handed over to it
        gameStateManager.localPlayerID = localPlayerID;

        textPlayers[0].text = "";
        textPlayers[1].text = "";

        uint readiedPlayers = 0;

        // Texts for who is playing
        for (uint i = 0; i < (teamsAllowed == 1 ? 4 : 2); i++)
        {
            if (playersReadyStatus[i])
            {
#if UNITY_EDITOR
                if ((i & 0x1U) == 1)
                {
                    textPlayers[1].text += "<UNITY_EDITOR>\n";

                    readiedPlayers |= 0x2u;
                }
                else
                {
                    textPlayers[0].text = "\n<UNITY_EDITOR>" + textPlayers[0].text;

                    readiedPlayers |= 0x1u;
                }
#else
         string dispname = player_apis[i].displayName;

         if( i == localPlayerID )
         {
            // Check for value stomping
            if( playerAPIs[i] != Networking.LocalPlayer )
            {
               localPlayerID = -1; 
               return;
            }

            // Show local player in italics
            dispname = "<i>" + dispname + "</i>";
         }

         if((i & 0x1U) == 1)
         {
            textPlayers[1].text += dispname + "\n";
            readiedPlayers |= 0x2;
         }
         else
         {
            textPlayers[0].text = "\n" + dispname + textPlayers[0].text;

            readiedPlayers |= 0x1;
         }
#endif
            }

            // Update join buttons
            if (localPlayerID >= 0)
            {
                if (i == (uint)localPlayerID)
                {
                    joinButtons[i].gameObject.SetActive(true);
                    joinButtons[i].sharedMaterial = low;
                }
                else
                {
                    joinButtons[i].gameObject.SetActive(false);
                }
            }
            else
            {
                if (playersReadyStatus[i])
                {
                    joinButtons[i].gameObject.SetActive(false);
                }
                else
                {
                    joinButtons[i].gameObject.SetActive(true);
                    joinButtons[i].sharedMaterial = high;
                }
            }
        }

        if ((readiedPlayers == 0x3u || gameModeID == 2u) && localPlayerID == 0)
        {
            start.sharedMaterial = high;
            startCollider.enabled = true;
        }
        else
        {
            start.sharedMaterial = low;
            startCollider.enabled = false;
        }
    }

    public override void OnPlayerLeft(VRCPlayerApi player) // TODO: Free these two subscriptions from this ifdef block.
    {
        // Lobby leader left so force a reset

        if (player == playerAPIs[0])
        {
            ResetInternalState();
        }
    }

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        if (player == Networking.LocalPlayer)
            return;

        if (localPlayerID == 0)
        {
            // Send newjoiner update
            uint data = 0x00U;

            // Compress players arr
            for (int i = 0; i < 4; i++)
            {
                if (playersReadyStatus[i])
                    data |= 0x1U << i;
            }

            // Send which player IDs is joined
            Send((byte)(sevenBallNewJoinPlayer | data));

            // Send all the other states they need to catch up on
            Send((byte)(sevenBallMenuLocation | menuLocation));
            Send((byte)(sevenBallCol | colourID));
            Send((byte)(sevenBallTeams | teamsAllowed));
            Send((byte)(sevenBallTimeLimit | timerID));
            Send((byte)(sevenBallGameMode | gameModeID));
        }
    }

#endif

#if USE_KING_LOBBY // TODO: Figure out what this ifdef is for.
    void OnPlayerJoined(VRCPlayerApi player) // TODO: Handle this duplicated subscription to OnPlayerJoined
    {
        // Ignore this if we are the one joining
        if(player == Networking.LocalPlayer) {
            return;
        }

        if(Networking.GetOwner(main.gameObject) == Networking.LocalPlayer)
        {
            // Send all the other states they need to catch up on
            Send((byte)(k_7b_menu_loc | menu_loc));
            Send((byte)(k_7b_ball_col | colour_id));
            Send((byte)(k_7b_timelimit | timer_id));
            Send((byte)(k_7b_gamemode | gamemode_id));
        }
    }
#endif


    private void ApexTransition()
    {
        ballMat.SetTexture("_MainTex", ballTextures[colourID]);

        for (int i = 0; i < 16; i++)
        {
            ballRenderers[i].transform.position = resetPositions[i];

            if (gameModeID == 1u)
            {
                if (i >= 10)
                {
                    ballRenderers[i].SetActive(false);
                }
                else
                {
                    ballRenderers[i].SetActive(true);
                }
            }
            else
            {
                ballRenderers[i].SetActive(true);
            }
        }

    }

#if USE_AUTH_LOBBY
    void RefreshLobby()
    {
        ViewPlayers();
    }
#endif


    // MENU BUTTON INPUTS
    // ===================================================================================================================================================================================

    public void _on_joinas()
    {
#if USE_AUTH_LOBBY
        int playerCount = 0;
        for (int i = 0; i < 4; i++)
        {
            if (playersReadyStatus[i])
            {
                playerCount++;
            }
        }

        if (playersReadyStatus[joinAsID])
        {
            if (localPlayerID == joinAsID)
            {
                // Normal lobby leave
                localPlayerID = -1;

                // Networked leave
                Send((byte)(sevenBallLeave | joinAsID));
            }
            else
            {
                Debug.LogError($"ht8b_menu: JoinAs: tried to join as player {joinAsID} but player {Networking.GetOwner(tokens[joinAsID]).displayName} was already registered there");
            }
        }
        else
        {
            if (localPlayerID != -1)
            {
                Debug.LogError($"ht8b_menu: JoinAs: Tried to join as player {joinAsID}, but already in the game as {localPlayerID}. UI is lagged.");
            }
            else
            {
                if (playerCount == 0 && joinAsID != 0)
                {
                    // Force first joiner to host
                    Debug.LogWarning("ht8b_menu: JoinAs: Switching to host automatically.");
                    joinAsID = 0;
                }

                // Normal lobby join
                localPlayerID = joinAsID;

                // This is simply a hack for name transmission cause yeee
                Networking.SetOwner(Networking.LocalPlayer, tokens[localPlayerID]);

                // Networked join
                Send((byte)(sevenBallJoin | joinAsID));
            }
        }
#endif
    }


    public void OnMenuChange()
    {
#if USE_AUTH_LOBBY
        // Only allow one way 0->1 change at the moment.
        if (localPlayerID == 0)
        {
#endif
            if (inMenuLocation == 1)
            {
                menuLocation = inMenuLocation;

                // Networked menu change
                Send((byte)(sevenBallMenuLocation | menuLocation));
            }
            else
            {
                Debug.LogError("ht8b_menu: OnMenuChange: Menu transitions other than 0->1 are not implemented");
            }
#if USE_AUTH_LOBBY
        }
        else
        {
            Debug.LogError("ht8b_menu: OnMenuChange: Cannot change menu state as we are not the lobby leader");
        }
#endif
    }

    // Change colourset

    public void OnColourChange()
    {
#if USE_AUTH_LOBBY
        if (localPlayerID == 0)
        {
#endif
            int newcol = (int)colourID + colourChangeDir;

            if (newcol < 0)
            {
                newcol = (int)maxColour;
            }

            if (newcol > maxColour)
            {
                newcol = 0;
            }

            colourID = (uint)newcol;

            // Networked colour change
            Send((byte)(sevenBallCol | colourID));

            // Local view
            ViewColours();
#if USE_AUTH_LOBBY
        }
        else
        {
            Debug.LogError("ht8b_menu: OnColourChange: Cannot change ball colours when not the lobby leader");
        }
#endif
    }


    public void OnTeamAllowChange()
    {
#if USE_AUTH_LOBBY
        if (localPlayerID == 0)
        {
#endif
            teamsAllowed = (uint)allowTeams;

            // Networked team allow
            Send((byte)(sevenBallTeams | teamsAllowed));

#if USE_AUTH_LOBBY
        }
        else
        {
            Debug.LogError("ht8b_menu: OnMenuChange: Cannot change team settings if not the lobby leader");
        }
#endif
    }


    public void _on_timelimitchange()
    {
#if USE_AUTH_LOBBY
        if (localPlayerID == 0)
        {
#endif

            timerID = (uint)timeLimitID;

            // Networked timelimit update
            Send((byte)(sevenBallTimeLimit | timerID));

#if USE_AUTH_LOBBY
        }
        else
        {
            Debug.LogError("ht8b_menu: OnTeamAllowChange: Cannot change time limit if not lobby leader");
        }
#endif
    }


    public void OnGameModeChange()
    {
#if USE_AUTH_LOBBY
        if (localPlayerID == 0)
        {
#endif
            // If 9 ball, disable colour
            if (inputGameModeID == 1)
            {
                colourID = 3U;
                ViewColours();
                Send((byte)(sevenBallCol | colourID));
            }
            else
            {
                // US is locked to gamemode 1 so we have to reset colour ID
                if (gameModeID == 1u)
                {
                    colourID = 0U;
                    ViewColours();
                    Send((byte)(sevenBallCol | colourID));
                }
            }

            gameModeID = (uint)inputGameModeID;

            // Networked gamemode change
            Send((byte)(sevenBallGameMode | gameModeID));

#if USE_AUTH_LOBBY
        }
        else
        {
            Debug.LogError("ht8b_menu: OnGameModeChange: Cannot change gamemode if not lobby leader");
        }
#endif
    }

    // ==============================================================================================================================================================================

#if USE_AUTH_LOBBY

    void RefreshUsersAPI()
    {
        for (int i = 0; i < 4; i++)
        {
            playerAPIs[i] = Networking.GetOwner(tokens[i]);
        }
    }

#endif

    // Send 7 bits over the network
    void Send(byte data)
    {
        if (isGameRunning)
        {
            Debug.LogError("ht8b_menu: Send: Tried to Send while game was running");
            return;
        }

        if ((data & 0x80U) > 0)
        {
            Debug.LogError("ht8b_menu: Send: Tried to send more than 7 bits");
            return;
        }

#if UNITY_EDITOR
        SendCustomEvent("B7" + data.ToString("X2"));
#else
        SendCustomNetworkEvent( VRC.Udon.Common.Interfaces.NetworkEventTarget.All, "B7" + data.ToString("X2"));
#endif
    }

    // Recieve 7 bits from network
    private void Receive(byte data)
    {
        if (!gameObject.activeSelf)
        {
            Debug.LogError("ht8b_menu: Receive: Tried to run while this gameobject was disabled");
            return;
        }

        uint msgID = data & 0x70U;

        if (msgID == sevenBallJoin)                                                 // EV 0x00: Player join
        {
#if USE_AUTH_LOBBY
            uint playerid = (data & 0x3U);
            playersReadyStatus[playerid] = true;

            ViewPlayers();
#else
            Debug.LogError("ht8b_menu: Receive: ht8b was compiled without auth lobby enabled");  
#endif
        }
        else if (msgID == sevenBallLeave)                                            // EV 0x01: Player leave
        {
#if USE_AUTH_LOBBY
            uint playerid = (data & 0x3U);

            if (playerid == 0x00)
            {
                ResetInternalState();
            }
            else
            {
                playersReadyStatus[playerid] = false;
            }
            ViewPlayers();
#else
      Debug.LogError("ht8b_menu: Receive: ht8b was compiled without auth lobby enabled");  
#endif
        }
        else if (msgID == sevenBallNewJoinPlayer)                                          // EV 0x02: New join, playing status update
        {
#if USE_AUTH_LOBBY
            for (int i = 0; i < 4; i++)
            {
                playersReadyStatus[i] = ((data >> i) & 0x1) > 0;
            }

            ViewPlayers();
#else
      Debug.LogError("ht8b_menu: Receive: ht8b was compiled without auth lobby enabled");  
#endif
        }
        else if (msgID == sevenBallMenuLocation)                                          // EV 0x03: Menu location change
        {
            menuLocation = data & 0xfu;
            ViewMenu();
        }
        else if (msgID == sevenBallCol)                                          // EV 0x04: Ball colours
        {
            uint newid = (data & 0x3U);
            if (newid != colourID)
            {
                colourID = newid;
                ViewColours();
            }
        }
        else if (msgID == sevenBallGameMode)                                          // Ev 0x05: Gamemode
        {
            gameModeID = data & 0x3u;

            ViewGameMode();
#if USE_AUTH_LOBBY
            ViewPlayers();
#endif
        }
        else if (msgID == sevenBallTimeLimit)                                         // EV 0x06: Timelimit
        {
            timerID = data & 0x3U;

            if (timerID == 3U)
            {
                Debug.LogError("ht8b_menu: Receive: got timer ID 3, this is undefined. Something went wrong in network transmission");
                timerID = 2U;
            }

            ViewTimeLimit();
        }
        else if (msgID == sevenBallTeams)                                             // EV 0x07: Teams
        {
#if USE_AUTH_LOBBY
            teamsAllowed = data & 0x1U;

            if (teamsAllowed == 0 && localPlayerID > 1)
            {
                Debug.LogWarning("ht8b_menu: Receive: Auth lobby enabled and too many local players.");
                Send((byte)(sevenBallLeave | (uint)localPlayerID));

                localPlayerID = -1;
            }

            ViewTeam();
            ViewPlayers();
#else

      Debug.LogError("ht8b_menu: Receive: ht8b was compiled without auth lobby enabled");  

#endif
        }
        else
        {
            Debug.LogError($"ht8b_menu: Receive: Unknown message ID {msgID}");
        }
    }

    // VRChat
    public void B700() { Receive(0x0); }
    public void B701() { Receive(0x1); }
    public void B702() { Receive(0x2); }
    public void B703() { Receive(0x3); }
    public void B704() { Receive(0x4); }
    public void B705() { Receive(0x5); }
    public void B706() { Receive(0x6); }
    public void B707() { Receive(0x7); }
    public void B708() { Receive(0x8); }
    public void B709() { Receive(0x9); }
    public void B70A() { Receive(0xa); }
    public void B70B() { Receive(0xb); }
    public void B70C() { Receive(0xc); }
    public void B70D() { Receive(0xd); }
    public void B70E() { Receive(0xe); }
    public void B70F() { Receive(0xf); }
    public void B710() { Receive(0x10); }
    public void B711() { Receive(0x11); }
    public void B712() { Receive(0x12); }
    public void B713() { Receive(0x13); }
    public void B714() { Receive(0x14); }
    public void B715() { Receive(0x15); }
    public void B716() { Receive(0x16); }
    public void B717() { Receive(0x17); }
    public void B718() { Receive(0x18); }
    public void B719() { Receive(0x19); }
    public void B71A() { Receive(0x1a); }
    public void B71B() { Receive(0x1b); }
    public void B71C() { Receive(0x1c); }
    public void B71D() { Receive(0x1d); }
    public void B71E() { Receive(0x1e); }
    public void B71F() { Receive(0x1f); }
    public void B720() { Receive(0x20); }
    public void B721() { Receive(0x21); }
    public void B722() { Receive(0x22); }
    public void B723() { Receive(0x23); }
    public void B724() { Receive(0x24); }
    public void B725() { Receive(0x25); }
    public void B726() { Receive(0x26); }
    public void B727() { Receive(0x27); }
    public void B728() { Receive(0x28); }
    public void B729() { Receive(0x29); }
    public void B72A() { Receive(0x2a); }
    public void B72B() { Receive(0x2b); }
    public void B72C() { Receive(0x2c); }
    public void B72D() { Receive(0x2d); }
    public void B72E() { Receive(0x2e); }
    public void B72F() { Receive(0x2f); }
    public void B730() { Receive(0x30); }
    public void B731() { Receive(0x31); }
    public void B732() { Receive(0x32); }
    public void B733() { Receive(0x33); }
    public void B734() { Receive(0x34); }
    public void B735() { Receive(0x35); }
    public void B736() { Receive(0x36); }
    public void B737() { Receive(0x37); }
    public void B738() { Receive(0x38); }
    public void B739() { Receive(0x39); }
    public void B73A() { Receive(0x3a); }
    public void B73B() { Receive(0x3b); }
    public void B73C() { Receive(0x3c); }
    public void B73D() { Receive(0x3d); }
    public void B73E() { Receive(0x3e); }
    public void B73F() { Receive(0x3f); }
    public void B740() { Receive(0x40); }
    public void B741() { Receive(0x41); }
    public void B742() { Receive(0x42); }
    public void B743() { Receive(0x43); }
    public void B744() { Receive(0x44); }
    public void B745() { Receive(0x45); }
    public void B746() { Receive(0x46); }
    public void B747() { Receive(0x47); }
    public void B748() { Receive(0x48); }
    public void B749() { Receive(0x49); }
    public void B74A() { Receive(0x4a); }
    public void B74B() { Receive(0x4b); }
    public void B74C() { Receive(0x4c); }
    public void B74D() { Receive(0x4d); }
    public void B74E() { Receive(0x4e); }
    public void B74F() { Receive(0x4f); }
    public void B750() { Receive(0x50); }
    public void B751() { Receive(0x51); }
    public void B752() { Receive(0x52); }
    public void B753() { Receive(0x53); }
    public void B754() { Receive(0x54); }
    public void B755() { Receive(0x55); }
    public void B756() { Receive(0x56); }
    public void B757() { Receive(0x57); }
    public void B758() { Receive(0x58); }
    public void B759() { Receive(0x59); }
    public void B75A() { Receive(0x5a); }
    public void B75B() { Receive(0x5b); }
    public void B75C() { Receive(0x5c); }
    public void B75D() { Receive(0x5d); }
    public void B75E() { Receive(0x5e); }
    public void B75F() { Receive(0x5f); }
    public void B760() { Receive(0x60); }
    public void B761() { Receive(0x61); }
    public void B762() { Receive(0x62); }
    public void B763() { Receive(0x63); }
    public void B764() { Receive(0x64); }
    public void B765() { Receive(0x65); }
    public void B766() { Receive(0x66); }
    public void B767() { Receive(0x67); }
    public void B768() { Receive(0x68); }
    public void B769() { Receive(0x69); }
    public void B76A() { Receive(0x6a); }
    public void B76B() { Receive(0x6b); }
    public void B76C() { Receive(0x6c); }
    public void B76D() { Receive(0x6d); }
    public void B76E() { Receive(0x6e); }
    public void B76F() { Receive(0x6f); }
    public void B770() { Receive(0x70); }
    public void B771() { Receive(0x71); }
    public void B772() { Receive(0x72); }
    public void B773() { Receive(0x73); }
    public void B774() { Receive(0x74); }
    public void B775() { Receive(0x75); }
    public void B776() { Receive(0x76); }
    public void B777() { Receive(0x77); }
    public void B778() { Receive(0x78); }
    public void B779() { Receive(0x79); }
    public void B77A() { Receive(0x7a); }
    public void B77B() { Receive(0x7b); }
    public void B77C() { Receive(0x7c); }
    public void B77D() { Receive(0x7d); }
    public void B77E() { Receive(0x7e); }
    public void B77F() { Receive(0x7f); }
}