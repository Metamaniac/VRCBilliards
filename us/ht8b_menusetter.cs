#define COMPILE_WITH_TESTS

using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class ht8b_menusetter : UdonSharpBehaviour
{

    public int colourSet = 0;
    public int gameMode = -1;
    public int timer = -1;
    public int joinPlayer = -1;
    public int menuLocation = -1;
    public int allowTeams = -1;

    public ht8b_menu menu;
    public ht8b main;

    public bool startGame = false;

#if COMPILE_WITH_TESTS
    public bool forceInteract = false;
#endif

    public override void Interact()
    {
        if (colourSet != 0)
        {
            menu.colourChangeDir = colourSet;
            menu.OnColourChange();
        }

        if (gameMode >= 0)
        {
            menu.inputGameModeID = gameMode;
            menu.OnGameModeChange();
        }

        if (timer >= 0)
        {
            menu.timeLimitID = timer;
            menu._on_timelimitchange();
        }

        if (startGame)
        {
            // This will disable menu
            main.StartNewGame();
        }

        if (allowTeams >= 0)
        {
            menu.allowTeams = allowTeams;
            menu.OnTeamAllowChange();
        }

        if (joinPlayer >= 0)
        {
            menu.joinAsID = joinPlayer;
            menu._on_joinas();
        }

        if (menuLocation >= 0)
        {
            menu.inMenuLocation = (uint)menuLocation;
            menu.OnMenuChange();
        }
    }

#if COMPILE_WITH_TESTS
    void Update()
    {
        if (forceInteract)
        {
            Interact();
            forceInteract = false;
        }
    }
#endif

}
