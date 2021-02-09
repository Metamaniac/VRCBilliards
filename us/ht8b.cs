/* 
    Original Networking Model Information is below:
	
	This implementation of 8 ball is based around passing ownership between clients who are
	playing the game. A player is 'registered' into the game when they have ownership of one
	of the two player 'totems'. In this implementation the totems are the pool cues themselves.

	When a turn ends, the player who is currently playing will pack information into the 
	networking string that the turn has been transferred, and once the remote client who is
	associated with the opposite cue receives the update, they will take ownership of the main
	script.

	The local player will have a 'permit' to shoot when it is their turn, which allows them
	to interact with the physics world. As soon as the cue ball is shot, the script calculates
	and compresses the necessary velocities and positions of the balls, and 1. sends that out
	to remote clients, and 2. decodes it the same way themselves. So effectively all players
	end up watching the exact same simulation at very close to the same time. In testing this
	was immediate as it could be with a GB -> USA connection.

 Information about the data:

	- Data is transfered using 1 Udon Synced string which is 82 bytes long, encoded to base64( 110 bytes )
	- Critical game states are packed into a bitmask at #19
	- Floating point positions are encoded/decoded as follows:
		Encode:
			Divide the value by the expected maximum range
			Multiply that by signed short max value ~32k
			Add signed short max
			Cast to ushort
		Decode:
			Cast ushort to float
			Subtract short max
			Divide by short max
			Multiply by the same range encoded with

	- Ball ID's are designed around bitmasks and are as follows:

	byte | Byte 0														| Byte 1														|
	bit  | x80 . x40 . x20 . x10 . x08 . x04 . x02	| x1 .. x80 . x40 . x20 . x10 . x08 . x04 | x02 | x01 |
	ball | 15	 14	 13    12    11    10    9    |  7     6     5     4     3    2     1   |  8  | cue |

 Networking Layout:

   Total size: 78 bytes over network // 39 C# wchar
 
   Address		What						Data type
  
	[ 0x00  ]	ball positions			(compressed quantized vec2's)
	[ 0x40  ]	cue ball velocity		^
	[ 0x44  ]	cue ball angular vel	^

	[ 0x4A  ]	sn_pocketed				uint16 bitmask ( above table )
				OR	sn_gmspec				| bit	#	| mask	| what				|
												| 0-3		| 0x0f	| fb_scores[ 0 ]	|
												| 4-7		| 0xf0	| fb_scores[ 1	]	|
	
	[ 0x4C  ]	game state flags		| bit #	| mask	| what				| 
												| 0		| 0x1		| sn_simulating	|
												| 1		| 0x2		| sn_turnid			|
												| 2		| 0x4		| sn_foul			|
												| 3		| 0x8		| sn_open			|
												| 4		| 0x10	| sn_playerxor		|
												| 5		| 0x20	| sn_gameover		|
												| 6		| 0x40	| sn_winnerid		|
												| 7		| 0x80	| sn_permit			|
												| 8-10	| 0x700	| sn_gamemode		|
												| 11		| 0x800  | sn_lobbyopen		|
												| 12		| 0x1000 | <reserved>		|
												| 13-14	| 0x6000 | sn_timer			|
												| 15		| 0x8000 | sn_allowteams	|
												
	[ 0x4E  ]	packet #					uint16
	[ 0x50  ]	gameid					uint16

 Physics Implementation:
	Physics are done in 2D to save instructions. The implementation is designed to be
	as numerically stable as possible (e.g. using linear algebra as much as possible to
	be explicit about what and where stuff collides ).

	Ball physic response is 100% pure elastic energy transfer, which even at one iteration
	per physics update seems to give plausible enough results. balls can behave like a 
	newtons cradle which is what we want.

	Edge collisions are a little contrived and the reason why the table can ONLY be placed
	at world origin. the table is divided into major and minor sections. some of the 
	calculations can be peeked at here: https://www.geogebra.org/m/jcteyvj6 . It is all
	straight line equations.
	
	There MAY be deviations between SOME client CPUs / platforms depending on the floating 
	point architecture, and who knows what the fuck C# will decide to do at runtime anyway. 
	However after some testing this seems rare enough that we could not observe any
	differences at all. If it does happen to be calculated differently, the remote clients
	will catch up with the players game anyway. I reckon this is most likely going to
	affect, if it does at all, only quest/pc cross-play and not much else.

	Physics are calculated on a fixed timestep, using accumulator model. If there is very
	low framerate physics may run at a slower timescale if it passes the threshold where
	maximum updates/frame is reached, but won't affect eventual outcome.
	
	The display balls have their position matched, and rotated based on pure rolling model.
*/

//#if !UNITY_ANDROID This is the correct flag to spot if something is on Quest or not

using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;
using System;
using UnityEngine.Rendering;

/// <summary>
/// Main Behaviour for the VRCBilliards 8Ball variant.
/// </summary>
public class ht8b : UdonSharpBehaviour
{

#if UNITY_ANDROID
    const float MAX_DELTA = 0.075f;					// Maximum steps/frame ( 5 ish )
#else
    const float MAX_DELTA = 0.1f;                     // Maximum steps/frame ( 8 )
#endif

    // Physics calculation constants (measurements are in meters)

    const float FIXED_TIME_STEP = 0.0125f;                    // time step in seconds per iteration
    const float FIXED_SUBSTEP = 0.00125f;
    const float TIME_ALPHA = 50.0f;                       // (unused) physics interpolation
    const float TABLE_WIDTH = 1.0668f;                    // horizontal span of table
    const float TABLE_HEIGHT = 0.6096f;                   // vertical span of table
    const float BALL_DIAMETRE = 0.06f;                        // width of ball
    const float BALL_PL_X = 0.03f;                        // break placement X
    const float BALL_PL_Y = 0.05196152422f;           // Break placement Y
    const float BALL_1OR = 33.3333333333f;            // 1 over ball radius
    const float BALL_RSQR = 0.0009f;                  // ball radius squared
    const float BALL_DSQR = 0.0036f;                  // ball diameter squared
    const float BALL_DSQRPE = 0.003598f;              // ball diameter squared plus epsilon
    const float POCKET_RADIUS = 0.09f;                        // Full diameter of pockets (exc ball radi)
    const float CUSHION_RSTT = 0.79f;                     // Coefficient of restituion against cushion
    const float ONE_OVER_ROOT_TWO = 0.70710678118f;            // 1 over root 2 (normalize +-1,+-1 vector)
    const float ONE_OVER_ROOT_FIVE = 0.4472135955f;         // 1 over root 5 (normalize +-1,+-2 vector)
    const float RANDOMIZE_F = 0.0001f;
    const float POCKET_DEPTH = 0.04f;                     // How far back (roughly) do pockets absorb balls after this point
    const float MIN_VELOCITY = 0.00005625f;               // SQUARED
    const float FRICTION_EFF = 0.99f;                     // How much to multiply velocity by each update
    const float F_SLIDE = 0.2f;                       // Friction coefficient of sliding
    const float F_ROLL = 0.01f;                       // Friction coefficient of rolling
    const float SPOT_POSITION_X = 0.5334f;                    // First X position of the racked balls
    const float SPOT_CAROM_X = 0.8001f;                   // Spot position for carom mode
    const float RACHEIGHT = -0.0702f;                   // Rack position on Y axis
    const float GRAVITY = 9.80665f;                   // Earths gravitational acceleration
    const float BALL_MASS = 0.16f;                        // Weight of ball in kg
    const string FRP_LOW = "<color=\"#ADADAD\">";
    const string FRP_ERR = "<color=\"#B84139\">";
    const string FRP_WARN = "<color=\"#DEC521\">";
    const string FRP_YES = "<color=\"#69D128\">";
    const string FRP_END = "</color>";
    Vector3 CONTACT_POINT = new Vector3(0.0f, -0.03f, 0.0f); // Vectors cannot be const.

#if UNITY_ANDROID
    uint ANDROID_UNIFORM_CLOCK = 0x00u;
    uint ANDROID_CLOCDIVIDER = 0x8u;
#endif

    const float SINA = 0.28078832987f;
    const float SINA2 = 0.07884208619f;
    const float COSA = 0.95976971915f;
    const float COSA2 = 0.92115791379f;
    const float EP1 = 1.79f;
    const float A = 21.875f;
    const float B = 6.25f;
    const float F = 1.72909790282f;

    // Shader uniforms
    const string uniformTableColour = "_EmissionColour";
    const string uniformScoreCardColour0 = "_Colour0";
    const string uniformScoreCardColour1 = "_Colour1";
    const string uniformScoreCardInfo = "_Info";
    const string uniformMarkerColour = "_Color";
    const string unofmrCueColour = "_EmissionColor";
    const float desktopCursorSpeed = 0.035f;

    const float I16_MAXf = 32767.0f;

    const int FRP_MAX = 32;
    int FRP_LEN = 0;
    int FRP_PTR = 0;
    string[] FRP_LINES = new string[32];

    public bool isRolling = false;

    [Header("Table Colours")]
    public Color tableBlue = new Color(0.0f, 0.75f, 1.75f, 1.0f);
    public Color tableOrange = new Color(1.75f, 0.25f, 0.0f, 1.0f);
    public Color tableRed = new Color(1.2f, 0.0f, 0.0f, 1.0f);
    public Color tableWhite = new Color(1.0f, 1.0f, 1.0f, 1.0f);
    public Color tableBlack = new Color(0.01f, 0.01f, 0.01f, 1.0f);
    public Color tableYellow = new Color(2.0f, 1.0f, 0.0f, 1.0f);
    public Color tablePink = new Color(2.0f, 0.0f, 1.5f, 1.0f);
    public Color tableGreen = new Color(0.0f, 2.0f, 0.0f, 1.0f);
    public Color tableLightBlue = new Color(0.3f, 0.6f, 1.0f, 1.0f);
    public Color markerOK = new Color(0.0f, 1.0f, 0.0f, 1.0f);
    public Color markerNotOK = new Color(1.0f, 0.0f, 0.0f, 1.0f);
    public Color gripColourActive = new Color(0.0f, 0.5f, 1.1f, 1.0f);
    public Color gripColourInactive = new Color(0.34f, 0.34f, 0.34f, 1.0f);
    public Color fabricGray = new Color(0.3f, 0.3f, 0.3f, 1.0f);
    public Color fabricRed = new Color(0.9f, 0.2f, 0.1f, 1.0f);
    public Color fabricBlue = new Color(0.1f, 0.6f, 1.0f, 1.0f);
    public Color fabricWhite = new Color(0.8f, 0.8f, 0.8f, 1.0f);
    public Color fabricGreen = new Color(0.15f, 0.75f, 0.3f, 1.0f);
    public Color aimAiming = new Color(0.7f, 0.7f, 0.7f, 1.0f);
    public Color aimLocked = new Color(1.0f, 1.0f, 1.0f, 1.0f);

    [Header("Cues")]
    public ht8b_cue[] gripControllers; // [FSP] [3/2/21] TODO: Rename all of these files/classes to actually be remotely sane

    [Header("Table Objects")]
    public GameObject[] ballsToRender;
    public GameObject cueTip;
    public GameObject guideline;
    public GameObject devhit;
    public GameObject[] playerTotems;
    public GameObject[] cueTips;
    public GameObject gameTable;
    public GameObject infBaseTransform;
    public GameObject marker;
    public GameObject infHowToStart;
    public GameObject marker9ball;
    public GameObject tableOverlayUI;
    public GameObject fxColliderBase;
    public GameObject pocketBlockers;
    public GameObject menuBase;
    public GameObject point4Ball;
    public GameObject[] cueRenderObjs;
    public GameObject select4Ball;

    // Meshes
    private Mesh[] cueball_meshes;
    private Mesh nineBall;
    private Mesh fourBallAdd;
    private Mesh fourBallMinus;

    // Texts
    private Text logText;
    private Text[] playerNames;
    private Text winMessage;
    private Text resetMessage;

    // Renderers
    public Renderer scoreCardRenderer;

    // Materials
    public Material ballMaterial;
    public Material tableMaterial;
    public Texture[] sets;
    public Material guidelineMat;
    public Material[] cueGrips;
    public Material markerMaterial;
    public Texture scoreCard8Ball;
    public Texture scoreCard4Ball;

    // Audio
    public GameObject audioSourcePoolContainer;
    public AudioSource cueTipSrc;
    public AudioClip introSfx;
    public AudioClip sinkSfx;
    public AudioClip[] hitsSfx;
    public AudioClip newTurnSfx;
    public AudioClip pointMadeSfx;
    public AudioClip buttonSfx;
    public AudioClip spinSfx;
    public AudioClip spinStopSfx;
    public AudioClip hitBallSfx;

    private AudioSource[] ballPool;

    //Reflection Probes
    public ReflectionProbe tableReflection;

    // Audio Components
    private AudioSource mainSrc;

    [UdonSynced] private string newState;     // dumpster fire
    private string oldState;

    byte[] networkData = new byte[0x52];

    // Networked gamestate
    //  data positions are marked as <#ushort>:<#bit> (<hexmask>) <description>S

    public bool gameIsSimulating = false;      // 19:0 (0x01)		True whilst balls are rolling
    public uint timerType = 0; // 19:13 (0x6000)	Timer ID 2 bit		{ 0: inf, 1: 10s, 2: 15s, 3: 30s, 4: 60s, 5: undefined }
    public bool isPlayerAllowedToPlay = false;      // 19:7 (0x80)		Permission for player to play
    public uint gameMode = 0;   // 19:8 (0x700)	Gamemode ID 3 bit	{ 0: 8 ball, 1: 9 ball, 2+: undefined }

    private uint ballPocketedState = 0x00U;       // 18:0 (0xffff)	Each bit represents each ball, if it has been pocketed or not
    private uint turnID = 0x00U;     // 19:1 (0x02)		Whos turn is it, 0 or 1
    private bool isFoul = false;       // 19:2 (0x04)		End-of-turn foul marker
    private bool isOpen = true;        // 19:3 (0x08)		Is the table open?
    private uint playerColours = 0x00;       // 19:4 (0x10)		What colour the players have chosen
    private bool isGameOver = true;        // 19:5 (0x20)		Game is complete
    private uint winnerID = 0x00U;       // 19:6 (0x40)		Who won the game if sn_gameover is set
    private bool isLobbyClosed = true;
    private bool isTeams = false;  // 19:15 (0x8000)	Teams on/off (1 bit)
    private ushort clock = 0;         // 20:0 (0xffff)	Current packet number, used for locking updates so we dont accidently go back.
                                      //							this behaviour was observed on some long connections so its necessary
    private ushort gameID = 0;           // 21:0 (0xffff)	Game number
    private ushort modeSpecficData = 0;           // 22:0 (0xffff)	Game mode specific information

    // Cannot making a struct in C#, therefore values are duplicated

    // for gamestate deltas
    private uint oldPocketed;
    private uint oldTurnID;
    private bool oldOpen;
    private bool oldGameOver;
    private ushort oldGameID;

    private uint oldGameMode;
    private uint oldTimer;
    private bool oldTeams;
    private bool oldLobbyClosed;

    // Local gamestates
    [HideInInspector]
    public bool isArmed = false;       // Player is hitting
    private bool isUpdateLocked = false;     // We are waiting for our local simulation to finish, before we unpack data
    private int isFirstHit = 0;            // The first ball to be hit by cue ball

    private int isSecondHit = 0;
    private int isThirdHit = 0;

    private bool isSimulatedByUs = false;     // If the simulation was initiated by us, only set from update

    private byte sn_wins0 = 0;          // Wins for player 0 (unused)
    private byte sn_wins1 = 0;          // Wins for player 1 (unused)

    private float introAminTimer = 0.0f;        // Ball dropper timer

    private bool ballsMoving = false;       // Tracker variable to see if balls are still on the go

    private bool isReposition = false;          // Repositioner is active
    private float repoMaxX = TABLE_WIDTH; // For clamping to table or set lower for kitchen

    private float timerEnd = 0.0f;     // What should the timer run out at
    private float timerRecp = 0.0f;       // 1 over time limit
    private bool isTimerRunning = false;

    private bool isParticleAlive = false;
    private float particleTime = 0.0f;

    private bool isMadePoint = false;
    private bool isMadeFoul = false;

    private bool isJapanese = false;
    private bool isKorean = false;

    private int[] scores = new int[2];

    private bool isGameModeZero = false;
    private bool isGameModeOne = false;
    private bool isGameModeTwo = false;
    private bool isGameModePractice = false;           // Game should run in practice mode
    private bool isRegionSelected = false;

    private bool isDesktopShootUI = false;

    // Values that will get sucked in from the menu
    public int localPlayerID = -1;
    private uint localTeamID = 0u;     // Interpreted value

    public Vector3[] ball_CO = new Vector3[16];    // Current positions
    public Vector3[] ball_V = new Vector3[16]; // Current velocities
    public Vector3[] ball_W = new Vector3[16]; // Angular velocities

    private Color tableSrcColour = new Color(1.0f, 1.0f, 1.0f, 1.0f);   // Runtime target colour
    private Color tableCurrentColour = new Color(1.0f, 1.0f, 1.0f, 1.0f);   // Runtime actual colour

    // 'Pointer' colours.
    private Color pointerColour0;     // Team 0
    private Color pointerColour1;     // Team 1
    private Color pointerColour2;     // No team / open / 9 ball
    private Color pointerColourErr;
    private Color pointerClothColour;

    private Vector3 deskTopCursor = new Vector3(0.0f, 2.0f, 0.0f);
    private Vector3 desktopHitCursor = new Vector3(0.0f, 0.0f, 0.0f);

    public GameObject desktopCursorObject;
    public GameObject desktopHitPosition;
    public GameObject desktopBase;
    public GameObject desktopQuad;
    public GameObject[] desktopStickBases;
    public GameObject desktopOverlayPower;
    public GameObject dE;

    bool isDesktopShootingIn = false;
    bool isDesktopSafeRemove = true;
    Vector3 desktopShootVector;
    Vector3 desktopSafeRemovePoint;
    float desktopShootReference = 0.0f;
    float desktopClampX = TABLE_WIDTH;
    float desktopClampY = TABLE_HEIGHT;
    bool isTurnLocalLive = false;

    bool isDesktopFrameIgnore = false;

    // Cue input tracking
    Vector3 cueLPos;
    Vector3 cueLLPos;
    Vector3 cueVDir;
    Vector3 cueShotDir;
    float cueFDir;

    Vector3 m_planenormal;
    float m_planedist;

    Vector3 m_cursor;
    bool m_desktop = true;

    const float mGmButtonW = 0.09345f;
    const float mGmButtonH = 0.034f;
    const float mSmolButtonR = 0.034f;

    const float mGmButtonA = 0.01026977f;     // Reset height

    public GameObject[] m_gamemode_buttons;
    public GameObject[] m_join_buttons;

    public GameObject[] m_teambuttons;
    public GameObject[] m_timebuttons;

    bool[] m_gm_buttonstates = new bool[4];
    public Mesh[] m_buttonmeshes;

    const int EButtonMesh_8ball = 0;
    const int EButtonMesh_9ball = 1;
    const int EButtonMesh_4ball = 2;
    const int EButtonMesh_reserved0 = 3;
    const int EButtonMesh_green = 4;
    const int EButtonMesh_red = 5;
    const int EButtonMesh_blue = 6;
    const int EButtonMesh_triangle = 7;
    const int EButtonMesh_join_0 = 8;
    const int EButtonMesh_join_1 = 9;
    const int EButtonMesh_play = 10;

    const uint ButtonState_None = 0x0u;
    const uint ButtonState_Pressing = 0x1u;
    const uint ButtonState_Triggered = 0x2u;
    const uint ButtonState_ShouldReset = 0x2u;
    const uint ButtonState_FrameMask = 0xFFFFFFFEu; // (~0x1u)

    // Current check dimensions ( pulled from above )
    float m_current_x = 0.0f;
    float m_current_y = 0.0f;
    Mesh m_current_outline;
    MeshFilter m_outline_filter;

    uint[] m_auto_btnstate = new uint[20];
    GameObject[] m_auto_btnobjs = new GameObject[20];
    int m_auto_id = -1;

    public GameObject m_gm_dkoutline;
    public GameObject[] m_playerslot_owners;

    // VFX stuff
    public GameObject m_TeamCover;
    public GameObject m_TimeLimitDisp;

    Vector3 m_TeamCover_target_s;
    Vector3 m_TeamCover_current_s;

    Vector3 m_TimeLimit_x_target;
    Vector3 m_TimeLimit_x_current;

    public GameObject m_menuLoc_main;
    public GameObject m_menuLoc_start;
    public GameObject m_newGameBtn;
    public Text[] m_lobbyNames;
    public GameObject[] rulePages;

    Vector3 m_menuLoc_sw;
    Vector3 m_menuLoc_swt;

    VRCPlayerApi localplayer;

#if MENU_DEV

    public GameObject m_devcursor;

#endif

#if UNITY_ANDROID
#else
    public Vector3 desktopTargetPos;             // Target for desktop aiming
#endif

    Vector2 rayCircleOutput;
    Vector3 raySphereOutput;

    uint lastViewTimer = 0u;
    bool isSoundSpinning = false;

    uint gameModeTarget = 0u;
    float gameModeMinHeight = Mathf.Infinity;

    VRC_Pickup.PickupHand menuHand = VRC_Pickup.PickupHand.None;

    float timeLast;
    float accumulation;

    private float nextRefresh = 0.0f;

    float shootAmt = 0.0f;

    int[] breaorder_8ball = { 9, 2, 10, 11, 1, 3, 4, 12, 5, 13, 14, 6, 15, 7, 8 };
    int[] breaorder_9ball = { 2, 3, 4, 5, 9, 6, 7, 8, 1 };
    int[] brearows_9ball = { 0, 1, 2, 1, 0 };

    private void Start()
    {
        mainSrc = this.GetComponent<AudioSource>();

        if (audioSourcePoolContainer != null) // Xiexe: Use a pool for audio instead of using the PlayClipAtPoint method because PlayClipAtPoint is buggy and VRC audio controls do not modify it.
            ballPool = audioSourcePoolContainer.GetComponentsInChildren<AudioSource>();

        InitializeMenu();
        CopyCurrentValuesToPrevious();

        cueRenderObjs[0].GetComponent<MeshRenderer>().sharedMaterial.SetColor(unofmrCueColour, tableBlack);
        cueRenderObjs[1].GetComponent<MeshRenderer>().sharedMaterial.SetColor(unofmrCueColour, tableBlack);

        guidelineMat.SetMatrix("_BaseTransform", this.transform.worldToLocalMatrix);

        if (tableReflection != null)
        {
            tableReflection.gameObject.SetActive(true);
            tableReflection.mode = ReflectionProbeMode.Realtime;
            tableReflection.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            tableReflection.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
            tableReflection.RenderProbe();
        }

        // turn off guideline
        DisableObjects();
    }

    private void Update()
    {
        // Physics step accumulator routine
        float time = Time.timeSinceLevelLoad;
        float timeDelta = time - timeLast;

        timeLast = time;

        if (isParticleAlive)
        {
            FloatyEval();
        }

        if (isDesktopShootUI)
        {
            UpdateDesktopUI();
        }

        // Run sim only if things are moving
        if (gameIsSimulating)
        {
            accumulation += timeDelta;

            if (accumulation > MAX_DELTA)
            {
                accumulation = MAX_DELTA;
            }

            while (accumulation >= FIXED_TIME_STEP)
            {
                AdvanceSimilationForAllBalls();
                accumulation -= FIXED_TIME_STEP;
            }
        }
        else
        {
            // Control is in menu behaviour
            if (isGameOver)
            {
                UpdateMenu();
                return;
            }
        }

        // Update rendering objects positions
        uint ball_bit = 0x1u;
        for (int i = 0; i < 16; i++)
        {
            if ((ball_bit & ballPocketedState) == 0x0u)
            {
                ballsToRender[i].transform.localPosition = ball_CO[i];
            }

            ball_bit <<= 1;
        }

        cueLPos = this.transform.InverseTransformPoint(cueTip.transform.position);
        Vector3 lpos2 = cueLPos;

        // if shot is prepared for next hit
        if (isPlayerAllowedToPlay)
        {
            bool isContact = false;

            if (isReposition)
            {
                // Clamp position to table / kitchen
                Vector3 temp = marker.transform.localPosition;
                temp.x = Mathf.Clamp(temp.x, -TABLE_WIDTH, repoMaxX);
                temp.z = Mathf.Clamp(temp.z, -TABLE_HEIGHT, TABLE_HEIGHT);
                temp.y = 0.0f;
                marker.transform.localPosition = temp;
                marker.transform.localRotation = Quaternion.identity;

                ball_CO[0] = temp;
                ballsToRender[0].transform.localPosition = temp;

                isContact = IsCueContacting();

                if (isContact)
                {
                    markerMaterial.SetColor(uniformMarkerColour, markerNotOK);
                }
                else
                {
                    markerMaterial.SetColor(uniformMarkerColour, markerOK);
                }
            }

            Vector3 cueball_pos = ball_CO[0];

            if (isArmed && !isContact)
            {
                float sweep_time_ball = Vector3.Dot(cueball_pos - cueLLPos, cueVDir);

                // Check for potential skips due to low frame rate
                if (sweep_time_ball > 0.0f && sweep_time_ball < (cueLLPos - lpos2).magnitude)
                {
                    lpos2 = cueLLPos + cueVDir * sweep_time_ball;
                }

                // Hit condition is when cuetip is gone inside ball
                if ((lpos2 - cueball_pos).sqrMagnitude < BALL_RSQR)
                {
                    Vector3 horizontal_force = lpos2 - cueLLPos;
                    horizontal_force.y = 0.0f;

                    // Compute velocity delta
                    float vel = (horizontal_force.magnitude / Time.deltaTime) * 1.5f;

                    // Clamp velocity input to 20 m/s ( moderate break speed )
                    ball_V[0] = cueShotDir * Mathf.Min(vel, 20.0f);

                    // Angular velocity: L=r(normalized)×p
                    Vector3 r = (raySphereOutput - cueball_pos) * BALL_1OR;
                    Vector3 p = cueVDir * vel;
                    ball_W[0] = Vector3.Cross(r, p) * -50.0f;

                    HitGenerically();
                }
            }
            else
            {
                cueVDir = this.transform.InverseTransformVector(cueTip.transform.forward);//new Vector2( cuetip.transform.forward.z, -cuetip.transform.forward.x ).normalized;

                // Get where the cue will strike the ball
                if (IsIntersectignWithSphere(lpos2, cueVDir, cueball_pos))
                {
                    guideline.SetActive(true);
                    devhit.SetActive(true);
                    devhit.transform.localPosition = raySphereOutput;

                    cueShotDir = cueVDir;
                    cueShotDir.y = 0.0f;

                    if (isDesktopShootUI)
                    {
                    }
                    else
                    {
                        // Compute deflection in VR mode
                        Vector3 scuffdir = (cueball_pos - raySphereOutput);
                        scuffdir.y = 0.0f;
                        cueShotDir += scuffdir.normalized * 0.17f;
                    }

                    cueFDir = Mathf.Atan2(cueShotDir.z, cueShotDir.x);

                    // Update the prediction line direction
                    guideline.transform.localPosition = ball_CO[0];
                    guideline.transform.localEulerAngles = new Vector3(0.0f, -cueFDir * Mathf.Rad2Deg, 0.0f);
                }
                else
                {
                    devhit.SetActive(false);
                    guideline.SetActive(false);
                }
            }
        }

        cueLLPos = lpos2;

        // Table outline colour
        if (isGameOver)
        {
            // Flashing if we won
#if !UNITY_ANDROID
            tableCurrentColour = tableSrcColour * (Mathf.Sin(Time.timeSinceLevelLoad * 3.0f) * 0.5f + 1.0f);
#endif

            infBaseTransform.transform.localPosition = new Vector3(0.0f, Mathf.Sin(Time.timeSinceLevelLoad) * 0.1f, 0.0f);
            infBaseTransform.transform.Rotate(Vector3.up, 90.0f * Time.deltaTime);
        }
        else
        {
#if !UNITY_ANDROID
            tableCurrentColour = Color.Lerp(tableCurrentColour, tableSrcColour, Time.deltaTime * 3.0f);
#else

		// Run uniform updates at a slower rate on android (/8)
		ANDROID_UNIFORM_CLOCK ++;

		if( ANDROID_UNIFORM_CLOCK >= ANDROID_CLOCDIVIDER )
		{
			tableCurrentColour = Color.Lerp( tableCurrentColour, tableSrcColour, Time.deltaTime * 24.0f );
			tableMaterial.SetColor( uniform_tablecolour, tableCurrentColour );

			ANDROID_UNIFORM_CLOCK = 0x00u;
		}

#endif
        }

        float time_percentage;
        if (isTimerRunning)
        {
            float timeleft = timerEnd - Time.timeSinceLevelLoad;

            if (timeleft < 0.0f)
            {
                OnLocalTimerEnd();
                time_percentage = 0.0f;
            }
            else
            {
                time_percentage = 1.0f - (timeleft * timerRecp);
            }
        }
        else
        {
            time_percentage = 0.0f;
        }

#if !UNITY_ANDROID
        tableMaterial.SetColor(uniformTableColour,
            new Color(tableCurrentColour.r, tableCurrentColour.g, tableCurrentColour.b, time_percentage));
#endif

        // Intro animation
        if (introAminTimer > 0.0f)
        {
            introAminTimer -= Time.deltaTime;

            Vector3 temp;
            float atime;
            float aitime;

            if (introAminTimer < 0.0f)
                introAminTimer = 0.0f;

            // Cueball drops late
            temp = ballsToRender[0].transform.localPosition;
            atime = Mathf.Clamp(introAminTimer - 0.33f, 0.0f, 1.0f);
            aitime = (1.0f - atime);
            temp.y = Mathf.Abs(Mathf.Cos(atime * 6.29f)) * atime * 0.5f;
            ballsToRender[0].transform.localPosition = temp;
            ballsToRender[0].transform.localScale = new Vector3(aitime, aitime, aitime);

            for (int i = 1; i < 16; i++)
            {
                temp = ballsToRender[i].transform.localPosition;
                atime = Mathf.Clamp(introAminTimer - 0.84f - (float)i * 0.03f, 0.0f, 1.0f);
                aitime = (1.0f - atime);

                temp.y = Mathf.Abs(Mathf.Cos(atime * 6.29f)) * atime * 0.5f;
                ballsToRender[i].transform.localPosition = temp;
                ballsToRender[i].transform.localScale = new Vector3(aitime, aitime, aitime);
            }
        }
    }

    // Wait for updates to the synced netstr
    public override void OnDeserialization()
    {
        if (!string.Equals(newState, oldState))
        {
            oldState = newState;

            // Check if local simulation is in progress, the event will fire off later when physics
            // are settled by the client
            if (gameIsSimulating)
            {
                isUpdateLocked = true;
            }
            else
            {
                // We are free to read this update
                ReadNetworkData();
            }
        }
    }

    public void PackNetworkDataLossily()
    {
        if (!isGameOver)
        {
            return;
        }

        // Game state
        uint flags = 0x20u;                     // bit #

        // Since v1.0.0
        flags |= gameMode << 8;              // 8  - 3 bits
        flags |= timerType << 13;                // 13 - 2 bits
        if (isTeams) flags |= 0x8000u;     // 15 - 1 bit
        if (isLobbyClosed) flags |= 0x800u;

        EncodeUint16(0x4C, (ushort)flags);

        clock = (ushort)(clock + 1u);
        EncodeUint16(0x4E, (ushort)(clock));
        EncodeUint16(0x50, gameID);

        newState = Convert.ToBase64String(networkData, Base64FormattingOptions.None);
    }

    // Encode all data of game state into netstr
    public void PackNetworkData(uint _turnid)
    {
        if (localPlayerID < 0)
        {
            return;
        }

        // Garuntee array size by reallocating.. because c#
        networkData = new byte[0x52];

        for (int i = 0; i < 16; i++)
        {
            EncodeVector3(i * 4, ball_CO[i], 2.5f);
        }

        // Cue ball velocity & angular velocity last
        EncodeVector3(0x40, ball_V[0], 50.0f);
        EncodeVector3Full(0x44, ball_W[0], 500.0f);

        if (isGameModeTwo)
        {
            // Encode player scores into gmspec
            modeSpecficData = (ushort)(((uint)scores[0]) & 0x0fu);
            modeSpecficData |= (ushort)((((uint)scores[1]) & 0x0fu) << 4);
            if (isKorean) modeSpecficData |= (ushort)0x100u;

            // 4 ball specifc ( no pocket info )
            EncodeUint16(0x4A, modeSpecficData);
        }
        else
        {
            // Encode pocketed imformation
            EncodeUint16(0x4A, (ushort)(ballPocketedState & 0x0000FFFFu));
        }

        // Game state
        uint flags = 0x0U;                      // bit #
        if (gameIsSimulating) flags |= 0x1U;   // 0
        flags |= _turnid << 1;                  // 1
        if (isFoul) flags |= 0x4U;         // 2
        if (isOpen) flags |= 0x8U;         // 3
        flags |= playerColours << 4;         // 4
        if (isGameOver) flags |= 0x20u;    // 5
        flags |= winnerID << 6;              // 6
        if (isPlayerAllowedToPlay) flags |= 0x80U;      // 7

        if (isLobbyClosed) flags |= 0x800u;

        // Since v1.0.0
        flags |= gameMode << 8;              // 8  - 3 bits
        flags |= timerType << 13;                // 13 - 2 bits
        if (isTeams) flags |= 0x8000u;     // 15 - 1 bit

        EncodeUint16(0x4C, (ushort)flags);

        // Player ID msb gets added to referee any discrepencies between clients
        // Higher order players get priority because it will be less common
        // to play 2v2, so we can save most packet id's for normal 1v1
        uint msb_playerid = ((uint)localPlayerID & 0x2u) >> 1;

        EncodeUint16(0x4E, (ushort)(clock + 1u + msb_playerid));
        EncodeUint16(0x50, gameID);

        newState = Convert.ToBase64String(networkData, Base64FormattingOptions.None);
    }

    // Decode networking string
    // TODO: Clean up this function
    public void ReadNetworkData()
    {
        // CHECK ERROR ===================================================================================================

        byte[] in_data = Convert.FromBase64String(newState);
        if (in_data.Length < 0x52)
        {
            return;
        }

        networkData = in_data;
        // Throw out updates that are possible errournous
        ushort nextid = DecodeUint16(0x4E);
        if (nextid <= clock)
        {
            return;
        }
        clock = nextid;

        // MAIN DECODE ===================================================================================================
        CopyCurrentValuesToPrevious();

        // Pocketed information
        // Ball positions, reset velocity

        for (int i = 0; i < 16; i++)
        {
            ball_V[i] = Vector3.zero;
            ball_W[i] = Vector3.zero;
            ball_CO[i] = DecodeVector3(i * 4, 2.5f);
        }

        ball_V[0] = DecodeVector3(0x40, 50.0f);
        ball_W[0] = DecodeVector3Full(0x44, 500.0f);

        ballPocketedState = DecodeUint16(0x4A);

        uint gamestate = DecodeUint16(0x4C);
        gameIsSimulating = (gamestate & 0x1U) == 0x1U;
        turnID = (gamestate & 0x2U) >> 1;
        isFoul = (gamestate & 0x4U) == 0x4U;
        isOpen = (gamestate & 0x8U) == 0x8U;
        playerColours = (gamestate & 0x10U) >> 4;
        isGameOver = (gamestate & 0x20U) == 0x20U;
        winnerID = (gamestate & 0x40U) >> 6;
        isPlayerAllowedToPlay = (gamestate & 0x80U) == 0x80U;
        isLobbyClosed = (gamestate & 0x800u) == 0x800u;

        // Since v1.0.0
        gameMode = (gamestate & 0x700u) >> 8;            // 3 bits
        timerType = (gamestate & 0x6000u) >> 13;         // 2 bits
        isTeams = (gamestate & 0x8000u) == 0x8000u;        //

        // TODO: allocate more bits to packet ID, less to game ID
        gameID = DecodeUint16(0x50);

        // Events ==========================================================================================================

        if (gameID > oldGameID && !isGameOver)
        {
            // EV: 1
            OnLocalNewGame();
        }

        // Check if turn was transferred
        if (turnID != oldTurnID)
        {
            // EV: 2
            OnLocalTurnChange();
        }

        // Table switches to closed
        if (oldOpen && !isOpen)
        {
            // EV: 3
            OnLocalTableClosed();
        }

        // Check if game is over
        if (!oldGameOver && isGameOver)
        {
            // EV: 4
            OnLocalGameOver();
            return;
        }

        if (isGameOver)
        {
            ViewTimer();
            ViewTeams();
            ViewGameMode();
            ViewJoin();
            ViewMenu();
            return;
        }

        // Effects colliders need to be turned off when not simulating
        // to improve pickups being glitchy
        if (gameIsSimulating)
        {
            fxColliderBase.SetActive(true);
        }
        else
        {
            fxColliderBase.SetActive(false);
        }

        if (isGameModeTwo)
        {
            modeSpecficData = DecodeUint16(0x4A);
            scores[0] = (int)(modeSpecficData & 0x0fu);
            scores[1] = (int)((modeSpecficData & 0xf0u) >> 4);

            isKorean = (modeSpecficData & 0x100u) == 0x100u;
            isJapanese = !isKorean;

            ballPocketedState = 0xFDF2u;
        }

        // Check this every read
        // Its basically 'turn start' event
        if (isPlayerAllowedToPlay)
        {
            bool isOurTurn = ((localPlayerID >= 0) && (localTeamID == turnID)) || isGameModePractice;

            // Check if teammate placed the positioner
            if (!isFoul)
            {
                isReposition = false;
                marker.SetActive(false);
            }

#if !UNITY_ANDROID
            if (isOurTurn)
            {
                // Update for desktop
                AllowHit();
            }
            else
            {
                DenyHit();
            }
#endif

            if (isGameModeOne)
            {
                int target = GetLowestNumberedBall(ballPocketedState);

                marker9ball.SetActive(true);
                marker9ball.transform.localPosition = ball_CO[target];
            }

#if !UNITY_ANDROID
            RackBalls();
#endif

            if (timerType > 0 && !isTimerRunning)
            {
                ResetTimer();
            }
        }
        else
        {
            marker9ball.SetActive(false);
            isTimerRunning = false;
            isMadePoint = false;
            isMadeFoul = false;
            isFirstHit = 0;
            isSecondHit = 0;
            isThirdHit = 0;

            // These dissapeared from v1.0.0 for some reason
            marker.SetActive(false);
            devhit.SetActive(false);
            guideline.SetActive(false);
        }

        OnLocalUpdateScoreCard();
    }

    // Resets local game state to defined state
    // TODO: Merge this with NewGame()
    public void SetupBreak()
    {
        gameIsSimulating = false;
        isOpen = true;
        isGameOver = false;
        playerColours = 0;
        winnerID = 0;

        // Cue ball
        ball_CO[0] = new Vector3(-SPOT_POSITION_X, 0.0f, 0.0f);
        ball_V[0] = Vector3.zero;

        // Start at spot

        if (isGameModeOne) // 9 ball
        {
            ballPocketedState = 0xFC00u;

            for (int i = 0, k = 0; i < 5; i++)
            {
                int rown = brearows_9ball[i];
                for (int j = 0; j <= rown; j++)
                {
                    ball_CO[breaorder_9ball[k++]] = new Vector3
                    (
                        SPOT_POSITION_X + (float)i * BALL_PL_Y + UnityEngine.Random.Range(-RANDOMIZE_F, RANDOMIZE_F),
                        0.0f,
                        (float)(-rown + j * 2) * BALL_PL_X + UnityEngine.Random.Range(-RANDOMIZE_F, RANDOMIZE_F)
                    );

                    ball_V[k] = Vector3.zero;
                    ball_W[k] = Vector3.zero;
                }
            }
        }
        else if (isGameModeTwo) // 4 ball
        {
            ballPocketedState = 0xFDF2u;

            ball_CO[0] = new Vector3(-SPOT_CAROM_X, 0.0f, 0.0f);
            ball_CO[9] = new Vector3(SPOT_CAROM_X, 0.0f, 0.0f);
            ball_CO[2] = new Vector3(SPOT_POSITION_X, 0.0f, 0.0f);
            ball_CO[3] = new Vector3(-SPOT_POSITION_X, 0.0f, 0.0f);

            ball_V[0] = Vector3.zero;
            ball_V[9] = Vector3.zero;
            ball_V[2] = Vector3.zero;
            ball_V[3] = Vector3.zero;

            ball_W[0] = Vector3.zero;
            ball_W[9] = Vector3.zero;
            ball_W[2] = Vector3.zero;
            ball_W[3] = Vector3.zero;
        }
        else // Normal 8 ball modes
        {
            ballPocketedState = 0x00u;

            for (int i = 0, k = 0; i < 5; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    ball_CO[breaorder_8ball[k++]] = new Vector3
                    (
                        SPOT_POSITION_X + (float)i * BALL_PL_Y + UnityEngine.Random.Range(-RANDOMIZE_F, RANDOMIZE_F),
                        0.0f,
                        (float)(-i + j * 2) * BALL_PL_X + UnityEngine.Random.Range(-RANDOMIZE_F, RANDOMIZE_F)
                    );

                    ball_V[k] = Vector3.zero;
                    ball_W[k] = Vector3.zero;
                }
            }
        }

        oldPocketed = ballPocketedState;
    }
    // Purpose: 
    //  Public methods which should are called from other behaviours

    // Player select 4 ball mode Japanese
    public void SelectedJapaneseFourBall()
    {
        isJapanese = true;
        isKorean = false;
        isRegionSelected = true;
        select4Ball.SetActive(false);

        StartNewGame();
    }

    public void SelectedKoreanFourBall()
    {
        isJapanese = false;
        isKorean = true;
        isRegionSelected = true;
        select4Ball.SetActive(false);

        StartNewGame();
    }

    // Player is holding input trigger
    public void StartHit()
    {
        // lock aim variables
        bool isOurTurn = ((localPlayerID >= 0) && (localTeamID == turnID)) || isGameModePractice;

        if (isOurTurn)
        {
            isArmed = true;

#if !UNITY_ANDROID
            guidelineMat.SetColor("_Colour", aimLocked);
#endif
        }
    }

    // Player stopped holding input trigger
    public void EndHit()
    {
        isArmed = false;

#if !UNITY_ANDROID
        guidelineMat.SetColor("_Colour", aimAiming);
#endif
    }

    // Player was moving cueball, place it down
    public void PlaceBall()
    {
        if (!IsCueContacting())
        {
            isReposition = false;
            marker.SetActive(false);

            isPlayerAllowedToPlay = true;
            isFoul = false;

            Networking.SetOwner(Networking.LocalPlayer, this.gameObject);

            // Save out position to remote clients
            PackNetworkData(turnID);
            ReadNetworkData();
        }
    }

    // Initialize new match as the host
    public void StartNewGame()
    {
        // Check if game in progress
        if (isGameOver)
        {
            // Get gamestate rolling
            gameID++;
            isPlayerAllowedToPlay = true;

            OnLocalNewGame();

            turnID = 0;
            oldTurnID = 0;
            OnLocalTurnChange();

            // Following is overrides of NewGameLocal, for game STARTER only
            SetupBreak();
            ApplyTableColour(0);

            Networking.SetOwner(Networking.LocalPlayer, this.gameObject);
            PackNetworkData(0);
            ReadNetworkData();

            // Override allow repositioning within kitchen
            // Local effector
            isReposition = true;
            repoMaxX = -SPOT_POSITION_X;
            marker.transform.localPosition = ball_CO[0];
            marker.SetActive(true);

            if (!isRegionSelected)
            {
                if (gameMode == 2u)
                {
                    select4Ball.SetActive(true);
                    return;
                }
            }
        }
    }

    // Completely reset ht8b state
    public void ForceReset()
    {
        // Limit reset to totem owners ownly, this will always be someone in the room
        // but it may not be obvious to players who has the ownership. So a info text
        // is added above the reset button telling them who can reset if they dont have it
        // this is simply to prevent trolls running in and force resetting at random

        if (Networking.LocalPlayer == Networking.GetOwner(playerTotems[0]) ||
            Networking.LocalPlayer == Networking.GetOwner(playerTotems[1])
            || isGameOver)
        {
            isGameOver = true;
            isPlayerAllowedToPlay = false;
            gameIsSimulating = false;

            // For good measure in case other clients trigger an event whilst owner
            clock += 2;

            Networking.SetOwner(Networking.LocalPlayer, this.gameObject);
            PackNetworkData(turnID);
            ReadNetworkData();

            OnLocalGameOver();

            resetMessage.text = "Reset";
        }
        else
        {
            // TODO: Make this a panel
            resetMessage.text = "Only:\n" + Networking.GetOwner(playerTotems[0]).displayName + " and " + Networking.GetOwner(playerTotems[1]).displayName + "\ncan reset";
        }
    }

    // Cue picked up local
    public void OnPickupCueLocally()
    {
        isDesktopShootUI = true;
        isDesktopFrameIgnore = true;
        desktopBase.SetActive(true);

        // Lock player in place
        Networking.LocalPlayer.SetWalkSpeed(0.0f);
        Networking.LocalPlayer.SetRunSpeed(0.0f);
        Networking.LocalPlayer.SetStrafeSpeed(0.0f);
    }

    // Cue put down local
    public void OnPutDownCueLocally()
    {
        OnDesktopUIExit();
    }

    public void UpdateColourSources()
    {
        if (isGameModeOne)    // 9 Ball / USA colours
        {
            pointerColour0 = tableLightBlue;
            pointerColour1 = tableLightBlue;
            pointerColour2 = tableLightBlue;

            pointerColourErr = tableBlack;    // No error effect
            pointerClothColour = fabricBlue;

            // 9 ball only uses one colourset / cloth colour
            ballMaterial.SetTexture("_MainTex", sets[3]);
        }
        else if (isGameModeTwo)
        {
            pointerColour0 = tableWhite;
            pointerColour1 = tableYellow;

            // Should not be used
            pointerColour2 = tableRed;
            pointerColourErr = tableRed;

            ballMaterial.SetTexture("_MainTex", sets[2]);
            pointerClothColour = fabricGreen;
        }
        else // Standard 8 ball derivatives
        {
            pointerColourErr = tableRed;
            pointerColour2 = tableWhite;

            pointerColour0 = tableBlue;
            pointerColour1 = tableOrange;

            ballMaterial.SetTexture("_MainTex", sets[0]);
            pointerClothColour = fabricGray;
        }
        tableMaterial.SetColor("_ClothColour", pointerClothColour);
        tableReflection.RenderProbe();
    }

    // Updates table colour target to appropriate player colour
    private void ApplyTableColour(uint idsrc)
    {
        if (isGameModeTwo)
        {
            if (turnID == 0)
            {
                cueRenderObjs[0].GetComponent<MeshRenderer>().sharedMaterial.SetColor(unofmrCueColour, pointerColour0);
                cueRenderObjs[1].GetComponent<MeshRenderer>().sharedMaterial.SetColor(unofmrCueColour, pointerColour1 * 0.5f);
            }
            else
            {
                cueRenderObjs[0].GetComponent<MeshRenderer>().sharedMaterial.SetColor(unofmrCueColour, pointerColour0 * 0.5f);
                cueRenderObjs[1].GetComponent<MeshRenderer>().sharedMaterial.SetColor(unofmrCueColour, pointerColour1);
            }

            tableSrcColour = tableBlack;
        }

        else if (isGameModeOne)
        {
            cueRenderObjs[turnID].GetComponent<MeshRenderer>().sharedMaterial.SetColor(unofmrCueColour, tableWhite);
            cueRenderObjs[turnID ^ 0x1u].GetComponent<MeshRenderer>().sharedMaterial.SetColor(unofmrCueColour, tableBlack);

            tableSrcColour = pointerColour2;
        }

        else
        {
            if (!isOpen)
            {
                if ((idsrc ^ playerColours) == 0)
                {
                    // Set table colour to blue
                    tableSrcColour = pointerColour0;
                }
                else
                {
                    // Table colour to orange
                    tableSrcColour = pointerColour1;
                }

                cueRenderObjs[playerColours].GetComponent<MeshRenderer>().sharedMaterial.SetColor(unofmrCueColour, pointerColour0);
                cueRenderObjs[playerColours ^ 0x1u].GetComponent<MeshRenderer>().sharedMaterial.SetColor(unofmrCueColour, pointerColour1);
            }
            else
            {
                tableSrcColour = pointerColour2;

                cueRenderObjs[turnID].GetComponent<MeshRenderer>().sharedMaterial.SetColor(unofmrCueColour, tableWhite);
                cueRenderObjs[turnID ^ 0x1u].GetComponent<MeshRenderer>().sharedMaterial.SetColor(unofmrCueColour, tableBlack);
            }

        }

        cueGrips[turnID].SetColor(uniformMarkerColour, gripColourActive);
        cueGrips[turnID ^ 0x1u].SetColor(uniformMarkerColour, gripColourInactive);
    }

    private void ShowBalls()
    {
        if (isGameModeOne)
        {
            for (int i = 0; i <= 9; i++)
                ballsToRender[i].SetActive(true);

            for (int i = 10; i < 16; i++)
                ballsToRender[i].SetActive(false);
        }
        else if (isGameModeTwo)
        {
            for (int i = 1; i < 16; i++)
                ballsToRender[i].SetActive(false);

            ballsToRender[0].SetActive(true);
            ballsToRender[2].SetActive(true);
            ballsToRender[3].SetActive(true);
            ballsToRender[9].SetActive(true);
        }
        else
        {
            for (int i = 0; i < 16; i++)
            {
                ballsToRender[i].SetActive(true);
            }
        }
    }

    private void DisableObjects()
    {
        guideline.SetActive(false);
        devhit.SetActive(false);
        infBaseTransform.SetActive(false);
        marker.SetActive(false);
        tableOverlayUI.SetActive(false);
        marker9ball.SetActive(false);
        point4Ball.SetActive(false);
        select4Ball.SetActive(false);
    }

    private void SpawnFloaty(Vector3 pos, Mesh m)
    {
        point4Ball.SetActive(true);
        isParticleAlive = true;
        particleTime = 0.1f;

        // orient to be looking at player
        Vector3 lpos = Networking.LocalPlayer.GetPosition();
        Vector3 delta = lpos - this.transform.TransformPoint(pos);
        float r = Mathf.Atan2(delta.x, delta.z);
        point4Ball.transform.localRotation = Quaternion.AngleAxis(r * Mathf.Rad2Deg, Vector3.up);

        // set position
        point4Ball.transform.localPosition = pos;

        // Set scale
        point4Ball.transform.localScale = Vector3.zero;

        point4Ball.GetComponent<MeshFilter>().sharedMesh = m;
    }

    private void FloatyEval()
    {
        float scale, s, v, e;

        // Evaluate time
        particleTime += Time.deltaTime * 0.25f;

        // Sustained step
        s = Mathf.Max(particleTime - 0.1f, 0.0f);
        v = Mathf.Min(particleTime * particleTime * 100.0f, 21.0f * s * Mathf.Exp(-15.0f * s));

        // Exponential step
        e = Mathf.Exp(-17.0f * Mathf.Pow(Mathf.Max(particleTime - 1.2f, 0.0f), 3.0f));

        scale = e * v * 2.0f;

        // Set scale
        point4Ball.transform.localScale = new Vector3(scale, scale, scale);

        // Set position
        Vector3 temp = point4Ball.transform.localPosition;
        temp.y = particleTime * 0.5f;
        point4Ball.transform.localPosition = temp;

        // Particle death
        if (particleTime > 2.0f)
        {
            isParticleAlive = false;
            point4Ball.SetActive(false);
        }
    }

    private void ResetTimer()
    {
        if (timerType == 0)
        {
            timerEnd = Time.timeSinceLevelLoad + 30.0f;
            timerRecp = 0.03333333333f;
        }
        else
        {
            timerEnd = Time.timeSinceLevelLoad + 60.0f;
            timerRecp = 0.01666666666f;
        }

        isTimerRunning = true;
    }

    private void OnLocalCaromPoint(Vector3 p)
    {
        isMadePoint = true;
        mainSrc.PlayOneShot(pointMadeSfx, 1.0f);

        scores[turnID]++;

        if (scores[turnID] > 10)
        {
            scores[turnID] = 10;
        }

        SpawnFloaty(p, fourBallAdd);
    }

    private void OnLocalCaromPenalize(Vector3 p)
    {
        isMadeFoul = true;
        //aud_main.PlayOneShot( snd_bad, 1.0f );

        scores[turnID]--;

        if (scores[turnID] < 0)
        {
            scores[turnID] = 0;
        }

        SpawnFloaty(p, fourBallMinus);
    }

    // Called when a player first sinks a ball whilst the table was previously open
    private void OnLocalTableClosed()
    {
        uint picker = turnID ^ playerColours;
        ApplyTableColour(turnID);
        OnLocalUpdateScoreCard();

        scoreCardRenderer.sharedMaterial.SetColor(uniformScoreCardColour0, playerColours == 0 ? pointerColour0 : pointerColour1);
        scoreCardRenderer.sharedMaterial.SetColor(uniformScoreCardColour1, playerColours == 1 ? pointerColour0 : pointerColour1);
    }

    // End of the game. Both with/loss
    private void OnLocalGameOver()
    {
        ApplyTableColour(winnerID);

        winMessage.text = Networking.GetOwner(playerTotems[winnerID]).displayName + " wins!";
        infBaseTransform.SetActive(true);
        marker9ball.SetActive(false);
        tableOverlayUI.SetActive(false);

#if !UNITY_ANDROID
        RackBalls();   // To make sure rigidbodies are completely off
#endif

        isReposition = false;
        marker.SetActive(false);

        OnLocalUpdateScoreCard();

        // Remove any access rights
        localPlayerID = -1;
        GrantCueAccess();

        EnterMenu();
    }

    private void OnLocalTurnChange()
    {
        // Effects
        ApplyTableColour(turnID);
        mainSrc.PlayOneShot(newTurnSfx, 1.0f);

        // Register correct cuetip
        cueTip = cueTips[turnID];

        bool isOurTurn = ((localPlayerID >= 0) && (localTeamID == turnID)) || isGameModePractice;

        if (isGameModeTwo) // 4 ball
        {
            // Swap cue ball and opponent cue
            Vector3 temp = ball_CO[0];
            ball_CO[0] = ball_CO[9];
            ball_CO[9] = temp;

            if (turnID == 0)
            {
                ballsToRender[0].GetComponent<MeshFilter>().sharedMesh = cueball_meshes[0];
                ballsToRender[9].GetComponent<MeshFilter>().sharedMesh = cueball_meshes[1];
            }
            else
            {
                ballsToRender[9].GetComponent<MeshFilter>().sharedMesh = cueball_meshes[0];
                ballsToRender[0].GetComponent<MeshFilter>().sharedMesh = cueball_meshes[1];
            }
        }
        else
        {
            // White was pocketed
            if ((ballPocketedState & 0x1u) == 0x1u)
            {
                ball_CO[0] = Vector3.zero;
                ball_V[0] = Vector3.zero;
                ball_W[0] = Vector3.zero;

                ballPocketedState &= 0xFFFFFFFEu;
            }
        }

        if (isOurTurn)
        {
            if (isFoul)
            {
                isReposition = true;
                repoMaxX = TABLE_WIDTH;
                marker.SetActive(true);

                marker.transform.localPosition = ball_CO[0];
            }
        }

        // Force timer reset
        if (timerType > 0)
        {
            ResetTimer();
        }
    }

    private void OnLocalUpdateScoreCard()
    {
        if (isGameModeTwo)
        {
            scoreCardRenderer.sharedMaterial.SetVector(uniformScoreCardInfo, new Vector4(scores[0] * 0.04681905f, scores[1] * 0.04681905f, 0.0f, 0.0f));
        }
        else
        {
            int[] counter0 = new int[2];

            uint temp = ballPocketedState;

            for (int j = 0; j < 2; j++)
            {
                for (int i = 0; i < 7; i++)
                {
                    if ((temp & 0x4) > 0)
                    {
                        counter0[j ^ playerColours]++;
                    }

                    temp >>= 1;
                }
            }

            // Add black ball if we are winning the thing
            if (isGameOver)
            {
                counter0[winnerID] += (int)((ballPocketedState & 0x2) >> 1);
            }

            scoreCardRenderer.sharedMaterial.SetVector(uniformScoreCardInfo, new Vector4(counter0[0] * 0.0625f, counter0[1] * 0.0625f, 0.0f, 0.0f));
        }
    }

    // Player scored an objective ball 
    private void OnLocalPocketObject()
    {
        // Make a bright flash
        tableCurrentColour *= 1.9f;

        mainSrc.PlayOneShot(sinkSfx, 1.0f);
    }

    // Player scored a foul ball (cue, non-objective or 8 before set cleared)
    private void OnLocalPocketFoulBall()
    {
        tableCurrentColour = pointerColourErr;

        mainSrc.PlayOneShot(sinkSfx, 1.0f);
    }

    // once balls stops rolling this is called
    private void OnLocalSimulationEnd()
    {
        gameIsSimulating = false;

        // Make sure we only run this from the client who initiated the move
        if (isSimulatedByUs)
        {
            isSimulatedByUs = false;

            // We are updating the game state so make sure we are network owner
            Networking.SetOwner(Networking.LocalPlayer, this.gameObject);

            // Owner state checks

            uint bmask = 0xFFFCu;
            uint emask = 0x0u;

            // Quash down the mask if table has closed
            if (!isOpen)
            {
                bmask = bmask & (0x1FCu << ((int)(playerColours ^ turnID) * 7));
                emask = 0x1FCu << ((int)(playerColours ^ turnID ^ 0x1U) * 7);
            }

            // Common informations
            bool isSetComplete = (ballPocketedState & bmask) == bmask;
            bool isScratch = (ballPocketedState & 0x1U) == 0x1U;

            // Append black to mask if set is done
            if (isSetComplete)
            {
                bmask |= 0x2U;
            }

            // These are the resultant states we can set for each mode
            // then the rest is taken care of
            bool
                isObjectiveSink,
                isOpponentSink,
                winCondition,
                foulCondition,
                deferLossCondition
            ;

            if (isGameModeZero)    // Standard 8 ball
            {
                isObjectiveSink = (ballPocketedState & bmask) > (oldPocketed & bmask);
                isOpponentSink = (ballPocketedState & emask) > (oldPocketed & emask);

                // Calculate if objective was not hit first
                bool isWrongHit = ((0x1U << isFirstHit) & bmask) == 0;

                bool is8Sink = (ballPocketedState & 0x2U) == 0x2U;

                winCondition = isSetComplete && is8Sink;
                foulCondition = isScratch || isWrongHit;

                deferLossCondition = is8Sink;
            }
            else if (isGameModeOne)   // 9 ball
            {
                // Rules are from: https://www.youtube.com/watch?v=U0SbHOXCtFw

                // Rule #1: Cueball must strike the lowest number ball, first
                bool isWrongHit = !(GetLowestNumberedBall(oldPocketed) == isFirstHit);

                // Rule #2: Pocketing cueball, is a foul

                // Win condition: Pocket 9 ball ( at anytime )
                winCondition = (ballPocketedState & 0x200u) == 0x200u;

                // this video is hard to follow so im just gonna guess this is right
                isObjectiveSink = (ballPocketedState & 0x3FEu) > (oldPocketed & 0x3FEu);

                isOpponentSink = false;
                deferLossCondition = false;

                foulCondition = isWrongHit || isScratch;

                // TODO: Implement rail contact requirement
            }
            else if (isGameModeTwo) // 4 ball
            {
                isObjectiveSink = isMadePoint;
                isOpponentSink = isMadeFoul;
                foulCondition = false;
                deferLossCondition = false;

                winCondition = scores[turnID] >= 10;
            }
            else // Unkown mode
            {
                isObjectiveSink = true;
                isOpponentSink = false;
                winCondition = false;
                foulCondition = false;
                deferLossCondition = false;

                if ((ballPocketedState & 0x1u) == 0x1u)
                {
                    isFoul = true;
                    OnLocalTurnChange();
                }
            }

            if (winCondition)
            {
                if (foulCondition)
                {
                    // Loss
                    OnTurnOverGameWon(turnID ^ 0x1U);
                }
                else
                {
                    // Win
                    OnTurnOverGameWon(turnID);
                }
            }
            else if (deferLossCondition)
            {
                // Loss
                OnTurnOverGameWon(turnID ^ 0x1U);
            }
            else if (foulCondition)
            {
                // Foul
                OnTurnOverFoul();
            }
            else if (isObjectiveSink && !isOpponentSink)
            {
                // Continue
                OnTurnOverContinue();
            }
            else
            {
                // Pass
                OnTurnOverPassed();
            }
        }

        // Check if there was a network update on hold
        if (isUpdateLocked)
        {
            isUpdateLocked = false;

            ReadNetworkData();
        }
    }

    private void OnLocalTimerEnd()
    {
        isTimerRunning = false;

        // We are holding the stick so propogate the change
        if (Networking.GetOwner(playerTotems[turnID]) == Networking.LocalPlayer)
        {
            Networking.SetOwner(Networking.LocalPlayer, this.gameObject);
            OnTurnOverFoul();
        }
        else
        {
            // All local players freeze until next target
            // can pick up and propogate timer end
            isPlayerAllowedToPlay = false;
        }
    }

    // Grant cue access if we are playing
    private void GrantCueAccess()
    {
        if (localPlayerID >= 0)
        {
            if (isGameModePractice)
            {
                gripControllers[0].AllowAccess();
                gripControllers[1].AllowAccess();
            }
            else
            {
                if ((localTeamID & 0x1) > 0)                       // Local player is 1, or 3
                {
                    gripControllers[1].AllowAccess();
                    gripControllers[0].DenyAccess();
                }
                else                                                            // Local player is 0, or 2
                {
                    gripControllers[0].AllowAccess();
                    gripControllers[1].DenyAccess();
                }
            }
        }
        else
        {
            gripControllers[0].DenyAccess();
            gripControllers[1].DenyAccess();
        }
    }

    // Some udon specific optimisations
    private void SetupOptimiziations()
    {
        isGameModeZero = gameMode == 0u;
        isGameModeOne = gameMode == 1u;
        isGameModeTwo = gameMode == 2u;
    }

    private void OnLocalNewGame()
    {
        SetupOptimiziations();

        // Calculate interpreted values from menu states
        if (localPlayerID >= 0)
            localTeamID = (uint)localPlayerID & 0x1u;

        // Disable menu
        ExitMenu();

        // Reflect menu-state settings (for late joiners)
        UpdateColourSources();
        ApplyTableColour(0);
        GrantCueAccess();

        // TODO: move to function
        if (isGameModeOne)    // 9 ball specific
        {
            scoreCardRenderer.gameObject.SetActive(false);
            marker9ball.SetActive(true);
        }
        else
        {
            scoreCardRenderer.gameObject.SetActive(true);
            marker9ball.SetActive(false);
        }

        if (isGameModeTwo) // 4 ball specific
        {
            pocketBlockers.SetActive(true);
            scoreCardRenderer.sharedMaterial.SetTexture("_MainTex", scoreCard4Ball);

            scoreCardRenderer.sharedMaterial.SetColor(uniformScoreCardColour0, pointerColour0);
            scoreCardRenderer.sharedMaterial.SetColor(uniformScoreCardColour1, pointerColour1);

            scores[0] = 0;
            scores[1] = 0;

            // Reset mesh filters on balls that change them
            ballsToRender[0].GetComponent<MeshFilter>().sharedMesh = cueball_meshes[0];
            ballsToRender[9].GetComponent<MeshFilter>().sharedMesh = cueball_meshes[1];
        }
        else
        {
            pocketBlockers.SetActive(false);
            scoreCardRenderer.sharedMaterial.SetTexture("_MainTex", scoreCard8Ball);

            // Reset mesh filters on balls that change them
            ballsToRender[0].GetComponent<MeshFilter>().sharedMesh = cueball_meshes[0];
            ballsToRender[9].GetComponent<MeshFilter>().sharedMesh = nineBall;
        }


        ShowBalls();

        // Reflect game state
        OnLocalUpdateScoreCard();
        isReposition = false;
        marker.SetActive(false);
        infBaseTransform.SetActive(false);

        // Effects
        introAminTimer = 2.0f;
        mainSrc.PlayOneShot(introSfx, 1.0f);

        // Player name texts
        string base_text = "";
        if (isTeams)
        {
            base_text = "Team ";
        }

        tableOverlayUI.SetActive(true);
        playerNames[0].text = base_text + Networking.GetOwner(playerTotems[0]).displayName;
        playerNames[1].text = base_text + Networking.GetOwner(playerTotems[1]).displayName;

        isTimerRunning = false;

        // Switch desktop/vr
        bool usr_desktop = !Networking.LocalPlayer.IsUserInVR();

#if !UNITY_ANDROID
        gripControllers[0].useDesktop = usr_desktop;
        gripControllers[1].useDesktop = usr_desktop;
#endif
    }

#if !UNITY_ANDROID
    // Finalize positions onto their rack spots
    private void RackBalls()
    {
        uint ball_bit = 0x1u;

        for (int i = 0; i < 16; i++)
        {
            ballsToRender[i].GetComponent<Rigidbody>().isKinematic = true;

            if ((ball_bit & ballPocketedState) == ball_bit)
            {
                ballsToRender[i].transform.localPosition = new Vector3(
                    ball_CO[i].x,
                    RACHEIGHT,
                    ball_CO[i].z
                );
            }

            ball_bit <<= 1;
        }
    }
#endif

    // Internal game state pocket and enable unity physics to play out the rest
    private void PocketBalls(int id)
    {
        uint total = 0U;

        // Get total for X positioning
        int count_extent = isGameModeOne ? 10 : 16;
        for (int i = 1; i < count_extent; i++)
        {
            total += (ballPocketedState >> i) & 0x1U;
        }

        // set this for later
        ball_CO[id].x = -0.9847f + (float)total * BALL_DIAMETRE;
        ball_CO[id].z = 0.768f;

        ballPocketedState ^= 1U << id;

        uint bmask = 0x1FCU << ((int)(turnID ^ playerColours) * 7);

        // Good pocket
        if (((0x1U << id) & ((bmask) | (isOpen ? 0xFFFCU : 0x0000U) | ((bmask & ballPocketedState) == bmask ? 0x2U : 0x0U))) > 0)
        {
            OnLocalPocketObject();
        }
        else
        {
            // bad
            OnLocalPocketFoulBall();
        }

#if !UNITY_ANDROID

        // VFX ( make ball move )
        Rigidbody body = ballsToRender[id].GetComponent<Rigidbody>();
        body.isKinematic = false;
        body.velocity = new Vector3(

            ball_V[id].x,
            0.0f,
            ball_V[id].z

        );

#else

	ballsToRender[ id ].transform.position = new Vector3(

		ball_CO[ id ].x,
		RACHEIGHT,
		ball_CO[ id ].y
	
	);
#endif
    }

    // Is cue touching another ball?
    private bool IsCueContacting()
    {
        // 8 ball, practice, portal
        if (gameMode != 1u)
        {
            // Check all
            for (int i = 1; i < 16; i++)
            {
                if ((ball_CO[0] - ball_CO[i]).sqrMagnitude < BALL_DSQR)
                {
                    return true;
                }
            }
        }
        else // 9 ball
        {
            // Only check to 9 ball
            for (int i = 1; i <= 9; i++)
            {
                if ((ball_CO[0] - ball_CO[i]).sqrMagnitude < BALL_DSQR)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Check pocket condition
    private void CheckPockets(int id)
    {
        float zz, zx;
        Vector3 A;

        A = ball_CO[id];

        // Setup major regions
        zx = Mathf.Sign(A.x);
        zz = Mathf.Sign(A.z);

        // Its in a pocket
        if (A.z * zz > TABLE_HEIGHT + POCKET_DEPTH || A.z * zz > A.x * -zx + TABLE_WIDTH + TABLE_HEIGHT + POCKET_DEPTH)
        {
            PocketBalls(id);
        }
    }

    // Apply cushion bounce
    private void ApplyBounceCushion(int id, Vector3 N)
    {
        // Mathematical expressions derived from: https://billiards.colostate.edu/physics_articles/Mathavan_IMechE_2010.pdf
        //
        // (Note): subscript gamma, u, are used in replacement of Y and Z in these expressions because
        // unicode does not have them.
        //
        // f = 2/7
        // f₁ = 5/7
        // 
        // Velocity delta:
        //   Δvₓ = −vₓ∙( f∙sin²θ + (1+e)∙cos²θ ) − Rωᵤ∙sinθ
        //   Δvᵧ = 0
        //   Δvᵤ = f₁∙vᵤ + fR( ωₓ∙sinθ - ωᵧ∙cosθ ) - vᵤ
        //
        // Aux:
        //   Sₓ = vₓ∙sinθ - vᵧ∙cosθ+ωᵤ
        //   Sᵧ = 0
        //   Sᵤ = -vᵤ - ωᵧ∙cosθ + ωₓ∙cosθ
        //   
        //   k = (5∙Sᵤ) / ( 2∙mRA ); 
        //   c = vₓ∙cosθ - vᵧ∙cosθ
        //
        // Angular delta:
        //   ωₓ = k∙sinθ
        //   ωᵧ = k∙cosθ
        //   ωᵤ = (5/(2m))∙(-Sₓ / A + ((sinθ∙c∙(e+1)) / B)∙(cosθ - sinθ));
        //
        // These expressions are in the reference frame of the cushion, so V and ω inputs need to be rotated

        // Reject bounce if velocity is going the same way as normal
        // this state means we tunneled, but it happens only on the corner
        // vertexes
        Vector3 source_v = ball_V[id];
        if (Vector3.Dot(source_v, N) > 0.0f)
        {
            return;
        }

        // Rotate V, W to be in the reference frame of cushion
        Quaternion rq = Quaternion.AngleAxis(Mathf.Atan2(-N.z, -N.x) * Mathf.Rad2Deg, Vector3.up);
        Quaternion rb = Quaternion.Inverse(rq);
        Vector3 V = rq * source_v;
        Vector3 W = rq * ball_W[id];

        Vector3 V1;
        Vector3 W1;
        float k, c, s_x, s_z;

        //V1.x = -V.x * ((2.0f/7.0f) * SINA2 + EP1 * COSA2) - (2.0f/7.0f) * BALL_PL_X * W.z * SINA;
        //V1.z = (5.0f/7.0f)*V.z + (2.0f/7.0f) * BALL_PL_X * (W.x * SINA - W.y * COSA) - V.z;
        //V1.y = 0.0f; 
        // (baked):
        V1.x = -V.x * F - 0.00240675711f * W.z;
        V1.z = 0.71428571428f * V.z + 0.00857142857f * (W.x * SINA - W.y * COSA) - V.z;
        V1.y = 0.0f;

        // s_x = V.x * SINA - V.y * COSA + W.z;
        // (baked): y component not used:
        s_x = V.x * SINA + W.z;
        s_z = -V.z - W.y * COSA + W.x * SINA;

        // k = (5.0f * s_z) / ( 2 * BALL_MASS * A ); 
        // (baked):
        k = s_z * 0.71428571428f;

        // c = V.x * COSA - V.y * COSA;
        // (baked): y component not used
        c = V.x * COSA;

        W1.x = k * SINA;

        //W1.z = (5.0f / (2.0f * BALL_MASS)) * (-s_x / A + ((SINA * c * EP1) / B) * (COSA - SINA));
        // (baked):
        W1.z = 15.625f * (-s_x * 0.04571428571f + c * 0.0546021744f);
        W1.y = k * COSA;

        // Unrotate result
        ball_V[id] += rb * V1;
        ball_W[id] += rb * W1;
    }

    // Pocketless table
    private void BallTableCarom(int id)
    {
        float zz, zx;
        Vector3 A;

        A = ball_CO[id];

        // Setup major regions
        zx = Mathf.Sign(A.x);
        zz = Mathf.Sign(A.z);

        if (A.x * zx > TABLE_WIDTH)
        {
            ball_CO[id].x = TABLE_WIDTH * zx;
            ApplyBounceCushion(id, Vector3.left * zx);
        }

        if (A.z * zz > TABLE_HEIGHT)
        {
            ball_CO[id].z = TABLE_HEIGHT * zz;
            ApplyBounceCushion(id, Vector3.back * zz);
        }
    }

    // TODO: inline this
    // Xiexe: I think that this is a rather cursed way to handle table edge collision detections and I think there's maybe a less cursed way to do it.
    private void HandleTableEdgeCollision(int id)
    {
        float zy, zx, zk, zw, d, k, i, j, l, r;
        Vector3 A, N;

        A = ball_CO[id];

        // REGIONS
        /*  
            *  QUADS:							SUBSECTION:				SUBSECTION:
            *    zx, zy:							zz:						zw:
            *																
            *  o----o----o  +:  1			\_________/				\_________/
            *  | -+ | ++ |  -: -1		     |	    /		              /  /
            *  |----+----|					  -  |  +   |		      -     /   |
            *  | -- | +- |						  |	   |		          /  +  |
            *  o----o----o						  |      |             /       |
            * 
            */

        // Setup major regions
        zx = Mathf.Sign(A.x);
        zy = Mathf.Sign(A.z);

        // within pocket regions
        if ((A.z * zy > (TABLE_HEIGHT - POCKET_RADIUS)) && (A.x * zx > (TABLE_WIDTH - POCKET_RADIUS) || A.x * zx < POCKET_RADIUS))
        {
            // Subregions
            zw = A.z * zy > A.x * zx - TABLE_WIDTH + TABLE_HEIGHT ? 1.0f : -1.0f;

            // Normalization / line coefficients change depending on sub-region
            if (A.x * zx > TABLE_WIDTH * 0.5f)
            {
                zk = 1.0f;
                r = ONE_OVER_ROOT_TWO;
            }
            else
            {
                zk = -2.0f;
                r = ONE_OVER_ROOT_FIVE;
            }

            // Collider line EQ
            d = zx * zy * zk; // Coefficient
            k = (-(TABLE_WIDTH * Mathf.Max(zk, 0.0f)) + POCKET_RADIUS * zw * Mathf.Abs(zk) + TABLE_HEIGHT) * zy; // Konstant

            // Check if colliding
            l = zw * zy;
            if (A.z * l > (A.x * d + k) * l)
            {
                // Get line normal
                N.x = zx * zk;
                N.z = -zy;
                N.y = 0.0f;
                N *= zw * r;

                // New position
                i = (A.x * d + A.z - k) / (2.0f * d);
                j = i * d + k;

                ball_CO[id].x = i;
                ball_CO[id].z = j;

                // Reflect velocity
                ApplyBounceCushion(id, N);
            }
        }
        else // edges
        {
            if (A.x * zx > TABLE_WIDTH)
            {
                ball_CO[id].x = TABLE_WIDTH * zx;
                ApplyBounceCushion(id, Vector3.left * zx);
            }

            if (A.z * zy > TABLE_HEIGHT)
            {
                ball_CO[id].z = TABLE_HEIGHT * zy;
                ApplyBounceCushion(id, Vector3.back * zy);
            }
        }
    }

    // Advance simulation 1 step for ball id
    private void AdvanceSimulationForBall(int id)
    {
        // Since v1.5.0
        Vector3 V = ball_V[id];
        Vector3 W = ball_W[id];
        Vector3 cv;

        // Equations derived from: http://citeseerx.ist.psu.edu/viewdoc/download?doi=10.1.1.89.4627&rep=rep1&type=pdf
        // 
        // R: Contact location with ball and floor aka: (0,-r,0)
        // µₛ: Slipping friction coefficient
        // µᵣ: Rolling friction coefficient
        // i: Up vector aka: (0,1,0)
        // g: Planet Earth's gravitation acceleration ( 9.80665 )
        // 
        // Relative contact velocity (marlow):
        //   c = v + R✕ω
        //
        // Ball is classified as 'rolling' or 'slipping'. Rolling is when the relative velocity is none and the ball is
        // said to be in pure rolling motion
        //
        // When ball is classified as rolling:
        //   Δv = -µᵣ∙g∙Δt∙(v/|v|)
        //
        // Angular momentum can therefore be derived as:
        //   ωₓ = -vᵤ/R
        //   ωᵧ =  0
        //   ωᵤ =  vₓ/R
        //
        // In the slipping state:
        //   Δω = ((-5∙µₛ∙g)/(2/R))∙Δt∙i✕(c/|c|)
        //   Δv = -µₛ∙g∙Δt(c/|c|)

        // Relative contact velocity of ball and table
        cv = V + Vector3.Cross(CONTACT_POINT, W);

        // Rolling is achieved when cv's length is approaching 0
        // The epsilon is quite high here because of the fairly large timestep we are working with
        if (cv.magnitude <= 0.1f)
        {
            //V += -F_ROLL * GRAVITY * FIXED_TIME_STEP * V.normalized;
            // (baked):
            V += -0.00122583125f * V.normalized;

            // Calculate rolling angular velocity
            W.x = -V.z * BALL_1OR;
            W.y = 0.0f;
            W.z = V.x * BALL_1OR;

            // Stopping scenario
            if (V.magnitude < 0.01f && W.magnitude < 0.2f)
            {
                W = Vector3.zero;
                V = Vector3.zero;
            }
            else
            {
                ballsMoving = true;
            }
        }
        else // Slipping
        {
            Vector3 nv = cv.normalized;

            // Angular slipping friction
            //W += ((-5.0f * F_SLIDE * 9.8f)/(2.0f * 0.03f)) * FIXED_TIME_STEP * Vector3.Cross( Vector3.up, nv );
            // (baked):
            W += -2.04305208f * Vector3.Cross(Vector3.up, nv);
            V += -F_SLIDE * 9.8f * FIXED_TIME_STEP * nv;

            ballsMoving = true;
        }

        ball_W[id] = W;
        ball_V[id] = V;
        ballsToRender[id].transform.Rotate(W.normalized, W.magnitude * FIXED_TIME_STEP * -Mathf.Rad2Deg, Space.World);

        uint ball_bit = 0x1U << id;

        // ball/ball collisions
        for (int i = id + 1; i < 16; i++)
        {
            ball_bit <<= 1;

            if ((ball_bit & ballPocketedState) != 0U)
                continue;

            Vector3 delta = ball_CO[i] - ball_CO[id];
            float dist = delta.magnitude;

            if (dist < BALL_DIAMETRE)
            {
                // Physics shit

                Vector3 normal = delta / dist;

                Vector3 velocityDelta = ball_V[id] - ball_V[i];

                float dot = Vector3.Dot(velocityDelta, normal);

                if (dot > 0.0f)
                {
                    Vector3 reflection = normal * dot;
                    ball_V[id] -= reflection;
                    ball_V[i] += reflection;

                    // Prevent sound spam if it happens
                    if (ball_V[id].sqrMagnitude > 0 && ball_V[i].sqrMagnitude > 0)
                    {
                        int clip = UnityEngine.Random.Range(0, hitsSfx.Length - 1);
                        float vol = Mathf.Clamp01(ball_V[id].magnitude * reflection.magnitude);
                        ballPool[id].transform.position = ballsToRender[id].transform.position;
                        ballPool[id].PlayOneShot(hitsSfx[clip], vol);
                    }

                    // First hit detected
                    if (id == 0)
                    {
                        if (isGameModeTwo)
                        {
                            if (isKorean)  // KR 사구 ( Sagu )
                            {
                                if (i == 9)
                                {
                                    if (!isMadeFoul)
                                    {
                                        OnLocalCaromPenalize(ball_CO[i]);
                                    }
                                }
                                else
                                {
                                    if (isFirstHit == 0)
                                    {
                                        isFirstHit = i;
                                    }
                                    else
                                    {
                                        if (i != isFirstHit)
                                        {
                                            if (isSecondHit == 0)
                                            {
                                                isSecondHit = i;
                                                OnLocalCaromPoint(ball_CO[i]);
                                            }
                                        }
                                    }
                                }
                            }
                            else // JP 四つ玉 ( Yotsudama )
                            {
                                if (isFirstHit == 0)
                                {
                                    isFirstHit = i;
                                }
                                else
                                {
                                    if (isSecondHit == 0)
                                    {
                                        if (i != isFirstHit)
                                        {
                                            isSecondHit = i;
                                            OnLocalCaromPoint(ball_CO[i]);
                                        }
                                    }
                                    else
                                    {
                                        if (isThirdHit == 0)
                                        {
                                            if (i != isFirstHit && i != isSecondHit)
                                            {
                                                isThirdHit = i;
                                                OnLocalCaromPoint(ball_CO[i]);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            if (isFirstHit == 0)
                            {
                                isFirstHit = i;
                            }
                        }
                    }
                }
            }
        }
    }

    // ( Since v0.2.0a ) Check if we can predict a collision before move update happens to improve accuracy
    private bool isCollisionPredictable()
    {
        // Get what will be the next position
        Vector3 originalDelta = ball_V[0] * FIXED_TIME_STEP;
        Vector3 norm = ball_V[0].normalized;

        Vector3 h;
        float lf, s, nmag;

        // Closest found values
        float minlf = 9999999.0f;
        int minid = 0;
        float mins = 0;

        uint ball_bit = 0x1U;

        // Loop balls look for collisions
        for (int i = 1; i < 16; i++)
        {
            ball_bit <<= 1;

            if ((ball_bit & ballPocketedState) != 0U)
                continue;

            h = ball_CO[i] - ball_CO[0];
            lf = Vector3.Dot(norm, h);
            s = BALL_DSQRPE - Vector3.Dot(h, h) + lf * lf;

            if (s < 0.0f)
                continue;

            if (lf < minlf)
            {
                minlf = lf;
                minid = i;
                mins = s;
            }
        }

        if (minid > 0)
        {
            nmag = minlf - Mathf.Sqrt(mins);

            // Assign new position if got appropriate magnitude
            if (nmag * nmag < originalDelta.sqrMagnitude)
            {
                ball_CO[0] += norm * nmag;
                return true;
            }
        }

        return false;
    }

    // Run one physics iteration for all balls
    private void AdvanceSimilationForAllBalls()
    {
        ballsMoving = false;

        uint ball_bit = 0x1u;

        // Cue angular velocity
        if ((ballPocketedState & 0x1U) == 0)
        {

            if (!isCollisionPredictable())
            {
                // Apply movement
                ball_CO[0] += ball_V[0] * FIXED_TIME_STEP;
            }

            AdvanceSimulationForBall(0);
        }

        // Run main simulation / inter-ball collision
        for (int i = 1; i < 16; i++)
        {
            ball_bit <<= 1;

            if ((ball_bit & ballPocketedState) == 0U)
            {
                ball_CO[i] += ball_V[i] * FIXED_TIME_STEP;

                AdvanceSimulationForBall(i);
            }
        }

        // Check if simulation has settled
        if (!ballsMoving)
        {
            if (gameIsSimulating)
            {
                OnLocalSimulationEnd();
            }

            return;
        }

        if (isGameModeTwo)
        {
            BallTableCarom(0);
            BallTableCarom(2);
            BallTableCarom(3);
            BallTableCarom(9);
        }
        else
        {
            ball_bit = 0x1U;

            // Run edge collision
            for (int i = 0; i < 16; i++)
            {
                if ((ball_bit & ballPocketedState) == 0U)
                    HandleTableEdgeCollision(i);

                ball_bit <<= 1;
            }
        }

        if (isGameModeTwo) return;

        ball_bit = 0x1U;

        // Run triggers
        for (int i = 0; i < 16; i++)
        {
            if ((ball_bit & ballPocketedState) == 0U)
            {
                CheckPockets(i);
            }

            ball_bit <<= 1;
        }
    }

    // Ray circle intersection
    // yes, its fixed size circle
    // Output is dispensed into the below variable
    // One intersection point only
    // This is not used in physics calcuations, only cue input
    private bool IsIntersectingWithCircle(Vector2 start, Vector2 dir, Vector2 circle)
    {
        Vector2 nrm = dir.normalized;
        Vector2 h = circle - start;
        float lf = Vector2.Dot(nrm, h);
        float s = BALL_RSQR - Vector2.Dot(h, h) + lf * lf;

        if (s < 0.0f) return false;

        s = Mathf.Sqrt(s);

        if (lf < s)
        {
            if (lf + s >= 0)
            {
                s = -s;
            }
            else
            {
                return false;
            }
        }

        rayCircleOutput = start + nrm * (lf - s);
        return true;
    }

    private bool IsIntersectignWithSphere(Vector3 start, Vector3 dir, Vector3 sphere)
    {
        Vector3 nrm = dir.normalized;
        Vector3 h = sphere - start;
        float lf = Vector3.Dot(nrm, h);
        float s = BALL_RSQR - Vector3.Dot(h, h) + lf * lf;

        if (s < 0.0f) return false;

        s = Mathf.Sqrt(s);

        if (lf < s)
        {
            if (lf + s >= 0)
            {
                s = -s;
            }
            else
            {
                return false;
            }
        }

        raySphereOutput = start + nrm * (lf - s);
        return true;
    }

    // Closest point on line from pos
    private Vector2 GetClosestPointOnLineFromPos(Vector2 start, Vector2 dir, Vector2 pos)
    {
        return start + dir * Vector2.Dot(pos - start, dir);
    }

    // Find the lowest numbered ball, that isnt the cue, on the table
    // This function finds the VISUALLY represented lowest ball,
    // since 8 has id 1, the search needs to be split
    private int GetLowestNumberedBall(uint field)
    {
        for (int i = 2; i <= 8; i++)
        {
            if (((field >> i) & 0x1U) == 0x00U)
                return i;
        }

        if (((field) & 0x2U) == 0x00U)
            return 1;

        for (int i = 9; i < 16; i++)
        {
            if (((field >> i) & 0x1U) == 0x00U)
                return i;
        }

        // ??
        return 0;
    }

    private void OnTurnOverGameWon(uint winner)
    {
        isGameOver = true;
        winnerID = winner;

        PackNetworkData(turnID);
        ReadNetworkData();

        OnLocalGameOver();
    }

    private void OnTurnOverPassed()
    {
        isPlayerAllowedToPlay = true;

        PackNetworkData(turnID ^ 0x1u);
        ReadNetworkData();
    }

    private void OnTurnOverFoul()
    {
        isFoul = true;
        isPlayerAllowedToPlay = true;

        PackNetworkData(turnID ^ 0x1U);
        ReadNetworkData();
    }

    private void OnTurnOverContinue()
    {
        // Close table if it was open ( 8 ball specific )
        if (isGameModeZero)
        {
            if (isOpen)
            {
                uint sinorange = 0;
                uint sinblue = 0;
                uint pmask = ballPocketedState >> 2;

                for (int i = 0; i < 7; i++)
                {
                    if ((pmask & 0x1u) == 0x1u)
                        sinblue++;

                    pmask >>= 1;
                }
                for (int i = 0; i < 7; i++)
                {
                    if ((pmask & 0x1u) == 0x1u)
                        sinorange++;

                    pmask >>= 1;
                }

                if (sinblue == sinorange)
                {
                    // Sunk equal amounts therefore still undecided
                }
                else
                {
                    if (sinblue > sinorange)
                    {
                        playerColours = turnID;
                    }
                    else
                    {
                        playerColours = turnID ^ 0x1u;
                    }

                    isOpen = false;
                    OnLocalTableClosed();
                }
            }
        }

        // Keep playing
        isPlayerAllowedToPlay = true;

        PackNetworkData(turnID);
        ReadNetworkData();
    }

    private Vector3 IntersectPlaneAndLine(Vector3 n, float d, Vector3 a, Vector3 b)
    {
        Vector3 ba = b - a;
        float nDotA = Vector3.Dot(n, a);
        float nDotBA = Vector3.Dot(n, ba);

        return a + (((d - nDotA) / nDotBA) * ba);
    }

    // Setup meshes on gameobject
    private void SetupMeshes(GameObject button, int variant, bool state)
    {
        button.GetComponent<MeshFilter>().sharedMesh = m_buttonmeshes[variant * 3 + (state ? 1 : 0)];
    }

    private void InitializeMenu()
    {
        // Setup button meshes
        SetupMeshes(m_gamemode_buttons[0], EButtonMesh_8ball, gameMode == 0u);
        SetupMeshes(m_gamemode_buttons[1], EButtonMesh_9ball, gameMode == 1u);
        SetupMeshes(m_gamemode_buttons[2], EButtonMesh_4ball, gameMode == 2u);

        SetupMeshes(m_join_buttons[0], EButtonMesh_join_0, false);
        SetupMeshes(m_join_buttons[1], EButtonMesh_join_1, false);

        //_htbtn_init( m_startbutton, EButtonMesh_play, false );

        SetupMeshes(m_teambuttons[0], EButtonMesh_red, !isTeams);
        SetupMeshes(m_teambuttons[1], EButtonMesh_green, isTeams);

        SetupMeshes(m_timebuttons[0], EButtonMesh_triangle, true);
        SetupMeshes(m_timebuttons[1], EButtonMesh_triangle, true);
        SetupMeshes(m_newGameBtn, EButtonMesh_play, true);

        m_outline_filter = m_gm_dkoutline.GetComponent<MeshFilter>();

        // Create surface plane
        m_planenormal = menuBase.transform.up;
        m_planedist = Vector3.Dot(menuBase.transform.position, m_planenormal);

        localplayer = Networking.LocalPlayer;

        m_gm_dkoutline.SetActive(false);

        isLobbyClosed = true;
        ViewTimer();
        ViewTeams();
        ViewGameMode();
        ViewMenu();
    }

    // View gamemode changes
    private void ViewGameMode()
    {
        for (int i = 0; i < m_gamemode_buttons.Length; i++)
        {
            if (gameMode == (uint)i)
            {
                m_gamemode_buttons[i].GetComponent<MeshFilter>().sharedMesh = m_buttonmeshes[(EButtonMesh_8ball + i) * 3 + 1];
            }
            else
            {
                m_gamemode_buttons[i].GetComponent<MeshFilter>().sharedMesh = m_buttonmeshes[(EButtonMesh_8ball + i) * 3];
            }
        }
    }

    private void ViewJoin()
    {
        int playernum = 0;

        if (!isLobbyClosed)
        {
            VRCPlayerApi host = Networking.GetOwner(m_playerslot_owners[0]);

            // Check out player names
            for (int i = 0; i < (isTeams ? 4 : 2); i++)
            {
                VRCPlayerApi player = Networking.GetOwner(m_playerslot_owners[i]);

                // Mark host
                if (i == 0)
                {
                    m_lobbyNames[i].text = "<color=\"#f2ecb8\">" + player.displayName + "</color>";
                    playernum++;
                }
                else
                {
                    // Its us
                    if (localPlayerID == i)
                    {
                        // Error: Local believes that we are in lobby, but someone else is there
                        if (player.playerId != Networking.LocalPlayer.playerId)
                        {
                            localPlayerID = -1;
                            m_lobbyNames[i].text = "<color=\"#ff0000\">ERROR</color>";
                        }
                        else
                        {
                            playernum++;
                            m_lobbyNames[i].text = "<color=\"#cae4ed\">" + player.displayName + "</color>";
                        }
                    }
                    else
                    {
                        // Player is joined
                        if (host.playerId != player.playerId)
                        {
                            m_lobbyNames[i].text = "<color=\"#ffffff\">" + player.displayName + "</color>";
                            playernum++;
                        }
                        else
                        {
                            m_lobbyNames[i].text = "";
                        }
                    }
                }
            }
        }

        isGameModePractice = localPlayerID == 0 && playernum == 1;

        // If in the game
        if (localPlayerID >= 0)
        {
            // Set our team button to the 'leave' button
            SetupMeshes(m_join_buttons[localTeamID], EButtonMesh_join_0 + (int)localTeamID, true);

            // Opposite button should become startgame/disabled, 'enabled' if player 0
            if (localPlayerID == 0)
            {
                m_join_buttons[1].SetActive(true);
                SetupMeshes(m_join_buttons[1], EButtonMesh_play, true);
            }
            else
            {
                m_join_buttons[localTeamID ^ 0x1u].SetActive(false);
            }
        }
        else // Otherwise, its just join buttons
        {
            m_join_buttons[0].SetActive(true);
            m_join_buttons[1].SetActive(true);
            SetupMeshes(m_join_buttons[0], EButtonMesh_join_0, false);
            SetupMeshes(m_join_buttons[1], EButtonMesh_join_1, false);
        }
    }

    private void ViewTimer()
    {
        if (lastViewTimer != timerType)
        {
            mainSrc.PlayOneShot(spinSfx);
            lastViewTimer = timerType;
            isSoundSpinning = true;
        }

        m_TimeLimit_x_target = new Vector3(-0.128f * (float)timerType, 0.0f, 0.0f);
    }

    private void ViewTeams()
    {
        m_TeamCover_target_s = isTeams ? new Vector3(0, 1, 1) : new Vector3(1, 1, 1);
        SetupMeshes(m_teambuttons[0], EButtonMesh_red, !isTeams);
        SetupMeshes(m_teambuttons[1], EButtonMesh_green, isTeams);

        ViewJoin();
    }

    private void ViewMenu()
    {
        if (isLobbyClosed)
        {
            m_menuLoc_swt = Vector3.one;
        }
        else
        {
            m_menuLoc_swt = Vector3.zero;
        }
    }

    private bool OnButtonPressed(GameObject btn, int typeid)
    {
        // Set automatic id's 
        m_auto_id++;
        m_auto_btnobjs[m_auto_id] = btn;

        Vector3 delta; Vector3 tmp_pos;
        delta = btn.transform.localPosition - m_cursor;

        if (Mathf.Abs(delta.x) < m_current_x && Mathf.Abs(delta.z) < m_current_y)
        {
            if (m_desktop)
            {
                // Visual transform
                if (Input.GetButton("Fire1"))
                {
                    tmp_pos = btn.transform.localPosition;
                    tmp_pos.y = 0.0f;
                    btn.transform.localPosition = tmp_pos;
                }

                m_gm_dkoutline.SetActive(true);
                m_gm_dkoutline.transform.localPosition = btn.transform.localPosition;
                m_gm_dkoutline.transform.localRotation = btn.transform.localRotation;

                m_outline_filter.sharedMesh = m_buttonmeshes[typeid * 3 + 2];

                // Actuation
                if (Input.GetButtonDown("Fire1"))
                {
                    ButtonPressed();
                    return true;
                }
            }
            else // VR
            {
                // Button range
                if (m_cursor.y < mGmButtonA && m_cursor.y > -0.1f)
                {
                    // Update visual position
                    tmp_pos = btn.transform.localPosition;
                    tmp_pos.y = Mathf.Clamp(m_cursor.y, 0.0f, tmp_pos.y);
                    btn.transform.localPosition = tmp_pos;

                    m_auto_btnstate[m_auto_id] |= ButtonState_Pressing;

                    if (m_cursor.y <= 0.0f) // Actuation
                    {
                        // Rising edge
                        if (m_auto_btnstate[m_auto_id] == ButtonState_Pressing)
                        {
                            m_auto_btnstate[m_auto_id] |= ButtonState_Triggered;

                            ButtonPressed();
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    // Join lobby
    private void JoinPlayer(int id)
    {
        localPlayerID = id;
        localTeamID = ((uint)id & 0x2u) >> 1;
        Networking.SetOwner(Networking.LocalPlayer, m_playerslot_owners[id]);

        ViewJoin();
    }

    // Join team locally
    private void JoinTeam(int id)
    {
        // Leave routine
        if (localPlayerID >= 0)
        {
            // Close lobby
            if (localPlayerID == 0)
            {
                if (id == 0)
                {
                    isLobbyClosed = true;
                    localPlayerID = -1;

                    ViewMenu();
                    Networking.SetOwner(Networking.LocalPlayer, this.gameObject);
                    PackNetworkDataLossily();
                }
                else
                {
                    isRegionSelected = false;
                    StartNewGame();
                    return;
                }
            }
            else
            {
                if ((int)localTeamID == id)
                {
                    // Set owner back to host
                    Networking.SetOwner(Networking.GetOwner(m_playerslot_owners[0]), m_playerslot_owners[localPlayerID]);

                    // Mark locally out of game
                    localPlayerID = -1;
                }
            }

            ViewJoin();
            return;
        }

        // Create new lobby
        if (isLobbyClosed)
        {
            // Assign other players to us to signify not joined
            Networking.SetOwner(Networking.LocalPlayer, m_playerslot_owners[1]);
            Networking.SetOwner(Networking.LocalPlayer, m_playerslot_owners[2]);
            Networking.SetOwner(Networking.LocalPlayer, m_playerslot_owners[3]);

            isLobbyClosed = false;

            Networking.SetOwner(Networking.LocalPlayer, this.gameObject);
            PackNetworkDataLossily();

            JoinPlayer(0);
            ViewMenu();
            return;
        }

        VRCPlayerApi gameHost = Networking.GetOwner(m_playerslot_owners[0]);

        // Check for open spot on team
        // Team 1
        if (id == 1)
        {
            if (Networking.GetOwner(m_playerslot_owners[1]).playerId == gameHost.playerId)
            {
                JoinPlayer(1);
            }
            else if (isTeams && (Networking.GetOwner(m_playerslot_owners[3]).playerId == gameHost.playerId))
            {
                JoinPlayer(3);
            }
        }

        // Team 2
        else if (isTeams && (Networking.GetOwner(m_playerslot_owners[2]).playerId == gameHost.playerId))
        {
            JoinPlayer(2);
        }
    }

    private void ResetNetwork()
    {
        isLobbyClosed = true;

        if (Networking.GetOwner(this.gameObject) == Networking.LocalPlayer)
        {
            Networking.SetOwner(Networking.LocalPlayer, m_playerslot_owners[0]);
            Networking.SetOwner(Networking.LocalPlayer, m_playerslot_owners[1]);
            Networking.SetOwner(Networking.LocalPlayer, m_playerslot_owners[2]);
            Networking.SetOwner(Networking.LocalPlayer, m_playerslot_owners[3]);

            PackNetworkDataLossily();
        }
    }

    // Find button target
    private void FindButtonTarget()
    {
        m_auto_id = -1;

        GameObject btn;

        // Join / Leave buttons
        m_current_x = mGmButtonW;
        m_current_y = mGmButtonH;

        if (isLobbyClosed)
        {
            if (OnButtonPressed(m_newGameBtn, EButtonMesh_play))
            {
                JoinTeam(0);
            }
        }
        else
        {
            for (int i = 0; i < m_join_buttons.Length; i++)
            {
                btn = m_join_buttons[i];

                if (OnButtonPressed(btn, EButtonMesh_join_0 + i))
                {
                    JoinTeam(i);
                }
            }

            if (localPlayerID == 0) // Host only
            {
                // Gamemode buttons
                m_current_x = mGmButtonW;
                m_current_y = mGmButtonH;

                for (int i = 0; i < m_gamemode_buttons.Length; i++)
                {
                    btn = m_gamemode_buttons[i];

                    if (OnButtonPressed(btn, EButtonMesh_8ball + i))
                    {
                        gameMode = (uint)i;
                        ViewGameMode();
                        PackNetworkDataLossily();
                    }
                }

                // Smol buttons
                m_current_x = mSmolButtonR;
                m_current_y = mSmolButtonR;

                // Timelimit buttons
                if (OnButtonPressed(m_timebuttons[1], EButtonMesh_triangle))
                {
                    if (timerType > 0)
                    {
                        timerType--;

                        ViewTimer();
                        PackNetworkDataLossily();
                    }
                }
                if (OnButtonPressed(m_timebuttons[0], EButtonMesh_triangle))
                {
                    if (timerType < 2)
                    {
                        timerType++;

                        ViewTimer();
                        PackNetworkDataLossily();
                    }
                }

                // Teams enabled buttons
                if (OnButtonPressed(m_teambuttons[0], EButtonMesh_red))
                {
                    isTeams = false;

                    // Kick players
                    Networking.SetOwner(Networking.LocalPlayer, m_playerslot_owners[2]);
                    Networking.SetOwner(Networking.LocalPlayer, m_playerslot_owners[3]);

                    ViewTeams();
                    PackNetworkDataLossily();
                }
                if (OnButtonPressed(m_teambuttons[1], EButtonMesh_green))
                {
                    isTeams = true;

                    ViewTeams();
                    PackNetworkDataLossily();
                }
            }
        }
    }

    private void ButtonPressed()
    {
        mainSrc.PlayOneShot(buttonSfx);

        if (menuHand != VRC_Pickup.PickupHand.None)
        {
            Networking.LocalPlayer.PlayHapticEventInHand(menuHand, 0.02f, 1.0f, 1.0f);
        }
    }

    private void BeginMenu()
    {
        Vector3 tmp_pos;
        GameObject btn;

        for (int i = 0; i <= m_auto_id; i++)
        {
            if (m_auto_btnstate[i] == ButtonState_ShouldReset)
            {
                m_auto_btnstate[i] = ButtonState_None;
            }

            // Reset button Y position
            btn = m_auto_btnobjs[i];
            tmp_pos = btn.transform.localPosition;
            tmp_pos.y = mGmButtonA;
            btn.transform.localPosition = tmp_pos;

            // Disables pressed so it can be re-set
            m_auto_btnstate[i] &= ButtonState_FrameMask;
        }
    }

    private void EnterMenu()
    {
        menuBase.SetActive(true);
        ResetNetwork();

        ViewTimer();
        ViewTeams();
        ViewGameMode();
        ViewJoin();
        ViewMenu();
    }

    private void ExitMenu()
    {
        isLobbyClosed = true;
        menuBase.SetActive(false);
    }

    private void UpdateMenu()
    {
        if (localplayer == null) // Removed the ifdef because rider didn't like it, just return if local player is null, means we're in editor.
            return;

        m_desktop = !Networking.LocalPlayer.IsUserInVR();

        if (Time.timeSinceLevelLoad > nextRefresh)
        {
            ViewJoin();
            nextRefresh = Time.timeSinceLevelLoad + 0.5f;
        }

        // Desktop: Project cursor onto plane
        if (m_desktop)
        {
            m_gm_dkoutline.SetActive(false);

            VRCPlayerApi.TrackingData hmd = localplayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            m_cursor = IntersectPlaneAndLine(m_planenormal, m_planedist, hmd.position, hmd.position + (hmd.rotation * Vector3.forward));

#if MENU_DEV
            m_devcursor.transform.position = m_cursor;
#endif

            // Localize m_cursor
            m_cursor = menuBase.transform.InverseTransformPoint(m_cursor);

            menuHand = VRC_Pickup.PickupHand.None;
            BeginMenu();
            FindButtonTarget();
        }
        else
        {
#if MENU_DEV
            m_devcursor.transform.position = localplayer.GetBonePosition(HumanBodyBones.RightIndexDistal);
#endif

            // Xiexe: Changed VR to use just the tracking data position, hopefully this feels alright.
            BeginMenu();
            menuHand = VRC_Pickup.PickupHand.Left;
            VRCPlayerApi.TrackingData leftHand = localplayer.GetTrackingData(VRCPlayerApi.TrackingDataType.LeftHand);
            m_cursor = menuBase.transform.InverseTransformPoint(leftHand.position);
            FindButtonTarget();

            menuHand = VRC_Pickup.PickupHand.Right;
            VRCPlayerApi.TrackingData rightHand = localplayer.GetTrackingData(VRCPlayerApi.TrackingDataType.RightHand);
            m_cursor = menuBase.transform.InverseTransformPoint(rightHand.position);
            FindButtonTarget();
        }

        // Update visual stuff
        m_TeamCover_current_s = Vector3.Lerp(m_TeamCover_current_s, m_TeamCover_target_s, Time.deltaTime * 5.0f);
        m_TimeLimit_x_current = Vector3.Lerp(m_TimeLimit_x_current, m_TimeLimit_x_target, Time.deltaTime * 5.0f);
        m_menuLoc_sw = Vector3.Lerp(m_menuLoc_sw, m_menuLoc_swt, Time.deltaTime * 5.0f);

        // Stop sound
        if (isSoundSpinning && Vector3.Distance(m_TimeLimit_x_current, m_TimeLimit_x_target) < 0.01f)
        {
            isSoundSpinning = false;
            mainSrc.PlayOneShot(spinStopSfx);
        }

        m_TeamCover.transform.localScale = m_TeamCover_current_s;
        m_TimeLimitDisp.transform.localPosition = m_TimeLimit_x_current;

        // Menu locations scale swap
        m_menuLoc_start.transform.localScale = m_menuLoc_sw;
        m_menuLoc_main.transform.localScale = Vector3.one - m_menuLoc_sw;
    }

    // Copy current values to previous values, no memcpy here >:(
    private void CopyCurrentValuesToPrevious()
    {
        // Init _prv states
        oldTurnID = turnID;
        oldOpen = isOpen;
        oldGameOver = isGameOver;
        oldGameID = gameID;

        // Since 1.0.0
        oldGameMode = gameMode;
        oldTimer = timerType;
        oldTeams = isTeams;
        oldLobbyClosed = isLobbyClosed;

        //sn_pocketed_prv = sn_pocketed;		this one needs to be independent 
        //sn_simulating_prv = sn_simulating;
        //sn_foul_prv = sn_foul;
        //sn_playerxor_prv = sn_playerxor;
        //sn_winnerid_prv = sn_winnerid;
        //sn_permit_prv = sn_permit;
    }

    private void OnDesktopUIExit()
    {
        isDesktopShootUI = false;
        desktopBase.SetActive(false);
        gripControllers[0].desktopPrimaryControl = true;
        gripControllers[1].desktopPrimaryControl = true;

        Networking.LocalPlayer.SetWalkSpeed(2.0f);
        Networking.LocalPlayer.SetRunSpeed(4.0f);
        Networking.LocalPlayer.SetStrafeSpeed(2.0f);
    }

    private void AllowHit()
    {
        isTurnLocalLive = true;

        // Reset hit point
        desktopHitCursor = Vector3.zero;
    }

    private void DenyHit()
    {
        isTurnLocalLive = false;
    }

    private void UpdateDesktopUI()
    {
        if (isDesktopFrameIgnore)
        {
            isDesktopFrameIgnore = false;
            return;
        }

        if (Input.GetKeyDown(KeyCode.E))
        {
            OnDesktopUIExit();
            return;
        }

        // Keep UI rendering
        VRCPlayerApi.TrackingData hmd = localplayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
        desktopQuad.transform.position = hmd.position + hmd.rotation * Vector3.forward;
        dE.transform.position = desktopQuad.transform.position;

        deskTopCursor.x = Mathf.Clamp
        (
            deskTopCursor.x + Input.GetAxis("Mouse X") * desktopCursorSpeed,
            -desktopClampX,
             desktopClampX
        );
        deskTopCursor.z = Mathf.Clamp
        (
            deskTopCursor.z + Input.GetAxis("Mouse Y") * desktopCursorSpeed,
            -desktopClampY,
             desktopClampY
        );

        if (isTurnLocalLive)
        {
            Vector3 ncursor = deskTopCursor;
            ncursor.y = 0.0f;
            Vector3 delta = ncursor - ball_CO[0];
            GameObject cue = desktopStickBases[turnID];

            if (Input.GetButton("Fire1"))
            {
                if (!isDesktopShootingIn)
                {
                    isDesktopShootingIn = true;

                    // Create shooting vector
                    desktopShootVector = delta.normalized;

                    // Project reference start point
                    desktopShootReference = Vector3.Dot(desktopShootVector, ncursor);

                    // Create copy of cursor for later
                    desktopSafeRemovePoint = deskTopCursor;

                    // Unlock cursor position from table
                    desktopClampX = Mathf.Infinity;
                    desktopClampY = Mathf.Infinity;
                }

                // Calculate shoot amount via projection
                shootAmt = desktopShootReference - Vector3.Dot(desktopShootVector, ncursor);
                isDesktopSafeRemove = shootAmt < 0.0f;

                shootAmt = Mathf.Clamp(shootAmt, 0.0f, 0.5f);

                // Set delta back to dkShootVector
                delta = desktopShootVector;

                // Disable cursor in shooting mode
                desktopCursorObject.SetActive(false);
            }
            else
            {
                // Trigger shot
                if (isDesktopShootingIn)
                {
                    // Shot cancel
                    if (isDesktopSafeRemove)
                    {

                    }
                    else // FIREEEEE 
                    {
                        // Fake hit ( kinda )
                        float vel = Mathf.Pow(shootAmt * 2.0f, 1.4f) * 9.0f;

                        ball_V[0] = desktopShootVector * vel;

                        Vector3 r_1 = (raySphereOutput - ball_CO[0]) * BALL_1OR;
                        Vector3 p = desktopShootVector.normalized * vel;
                        ball_W[0] = Vector3.Cross(r_1, p) * -25.0f;
                        cue.transform.localPosition = new Vector3(2000.0f, 2000.0f, 2000.0f);
                        isTurnLocalLive = false;
                        HitGenerically();
                    }

                    // Restore cursor position
                    deskTopCursor = desktopSafeRemovePoint;
                    desktopClampX = TABLE_WIDTH;
                    desktopClampY = TABLE_HEIGHT;

                    // 1-frame override to fix rotation
                    delta = desktopShootVector;
                }

                isDesktopShootingIn = false;
                shootAmt = 0.0f;
                desktopCursorObject.SetActive(true);
            }

            if (Input.GetKey(KeyCode.W))
            {
                desktopHitCursor += Vector3.forward * Time.deltaTime;
            }
            if (Input.GetKey(KeyCode.S))
            {
                desktopHitCursor += Vector3.back * Time.deltaTime;
            }
            if (Input.GetKey(KeyCode.A))
            {
                desktopHitCursor += Vector3.left * Time.deltaTime;
            }
            if (Input.GetKey(KeyCode.D))
            {
                desktopHitCursor += Vector3.right * Time.deltaTime;
            }

            // Clamp in circle
            if (desktopHitCursor.magnitude > 0.90f)
            {
                desktopHitCursor = desktopHitCursor.normalized * 0.9f;
            }

            desktopHitPosition.transform.localPosition = desktopHitCursor;

            // Get angle
            float ang = Mathf.Atan2(delta.x, delta.z);

            // Create rotation
            Quaternion xr = Quaternion.AngleAxis(10.0f, Vector3.right);
            Quaternion r = Quaternion.AngleAxis(ang * Mathf.Rad2Deg, Vector3.up);

            Vector3 worldHit = new Vector3(desktopHitCursor.x * BALL_PL_X, desktopHitCursor.z * BALL_PL_X, -0.89f - shootAmt);

            cue.transform.localRotation = r * xr;
            cue.transform.position = this.transform.TransformPoint(ball_CO[0] + (r * xr * worldHit));
        }

        desktopCursorObject.transform.localPosition = deskTopCursor;
        desktopOverlayPower.transform.localScale = new Vector3(1.0f - (shootAmt * 2.0f), 1.0f, 1.0f);
    }

    private void HitGenerically()
    {
        // Make sure repositioner is turned off if the player decides he just
        // wanted to hit it without putting it somewhere
        isReposition = false;
        marker.SetActive(false);
        devhit.SetActive(false);
        guideline.SetActive(false);

        // Remove locks
        EndHit();
        isPlayerAllowedToPlay = false;
        isFoul = false;    // In case did not drop foul marker

        // Commit changes
        gameIsSimulating = true;
        oldPocketed = ballPocketedState;

        // Make sure we definately are the network owner
        Networking.SetOwner(Networking.LocalPlayer, this.gameObject);

        PackNetworkData(turnID);
        ReadNetworkData();

        isSimulatedByUs = true;

        float vol = Mathf.Clamp(ball_V[0].magnitude * 0.1f, 0f, 0.6f);
        cueTipSrc.transform.position = cueTip.transform.position;
        cueTipSrc.PlayOneShot(hitBallSfx, vol);
    }

    private void EncodeUint16(int pos, ushort v)
    {
        networkData[pos] = (byte)(v & 0xff);
        networkData[pos + 1] = (byte)(((uint)v >> 8) & 0xff);
    }

    private ushort DecodeUint16(int pos)
    {
        return (ushort)(networkData[pos] | (((uint)networkData[pos + 1]) << 8));
    }

    // 4 char string from Vector2. Encodes floats in: [ -range, range ] to 0-65535
    private void EncodeVector3(int pos, Vector3 vec, float range)
    {
        EncodeUint16(pos, (ushort)((vec.x / range) * I16_MAXf + I16_MAXf));
        EncodeUint16(pos + 2, (ushort)((vec.z / range) * I16_MAXf + I16_MAXf));
    }

    // 6 char string from Vector3. Encodes floats in: [ -range, range ] to 0-65535
    private void EncodeVector3Full(int pos, Vector3 vec, float range)
    {
        EncodeUint16(pos, (ushort)((Mathf.Clamp(vec.x, -range, range) / range) * I16_MAXf + I16_MAXf));
        EncodeUint16(pos + 2, (ushort)((Mathf.Clamp(vec.y, -range, range) / range) * I16_MAXf + I16_MAXf));
        EncodeUint16(pos + 4, (ushort)((Mathf.Clamp(vec.z, -range, range) / range) * I16_MAXf + I16_MAXf));
    }

    // Decode 4 chars at index to Vector3 (x,z). Decodes from 0-65535 to [ -range, range ]
    private Vector3 DecodeVector3(int start, float range)
    {
        ushort _x = DecodeUint16(start);
        ushort _y = DecodeUint16(start + 2);

        float x = ((_x - I16_MAXf) / I16_MAXf) * range;
        float y = ((_y - I16_MAXf) / I16_MAXf) * range;

        return new Vector3(x, 0.0f, y);
    }

    // Decode 6 chars at index to Vector3. Decodes from 0-65535 to [ -range, range ]
    private Vector3 DecodeVector3Full(int start, float range)
    {
        ushort _x = DecodeUint16(start);
        ushort _y = DecodeUint16(start + 2);
        ushort _z = DecodeUint16(start + 4);

        float x = ((_x - I16_MAXf) / I16_MAXf) * range;
        float y = ((_y - I16_MAXf) / I16_MAXf) * range;
        float z = ((_z - I16_MAXf) / I16_MAXf) * range;

        return new Vector3(x, y, z);
    }

    private string StringToHex()
    {
        string str = "";

        for (int i = 0; i < networkData.Length; i += 2)
        {
            ushort v = DecodeUint16(i);
            str += v.ToString("X4");
        }

        return str;
    }
}