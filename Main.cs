using Godot;
using System;

// ==========================================================================
// Main
// The UI controller. Owns no chess logic of its own at all — it just wires
// Board's public events to visible UI elements (labels, panels, captured-
// piece icons, move history text) and forwards button presses back to
// Board's public methods. This keeps Board.cs free of any knowledge of
// specific UI node paths, and keeps Main.cs free of any chess rules.
//
// NOTE ON mainMenuButton/exitButton PATHS BELOW: these are currently
// looked up as children of ResultLabel ("...ResultLayout/ResultLabel/
// MainMenuButton"). If your actual scene tree has them as SIBLINGS of
// ResultLabel instead (both directly under ResultLayout), these two
// GetNode calls will throw "Node not found" at runtime — worth double
// checking the scene tree matches these exact paths before running.
// ==========================================================================
public partial class Main : Node2D
{
	private Board board;
	private Label turnLabel;
	private HFlowContainer capturedByWhiteContainer;
	private HFlowContainer capturedByBlackContainer;
	private Button restartButton;
	private Button undoButton;
	private Panel gameOverPanel;
	private Label resultLabel;
	private Label moveHistoryLabel;
	private int moveNumber = 1;
	private bool isWhiteMove = true;
	private Button loadButton;
	private Control gameLayout;

	// ---- Level-select screen ----
	private Panel levelSelectPanel;
	private Button humanVsHumanButton;
	private Button beginnerButton;
	private Button intermediateButton;
	private Button advancedButton;
	private Button masterButton;
	private Button grandMasterButton;
	private Button levelSelectLoadButton;

	// ---- Game-over screen extras ----
	private Button mainMenuButton;
	private Button exitButton;


	public override void _Ready()
	{
		// ---- Node lookups ----
		board = GetNode<Board>("Board");
		turnLabel = GetNode<Label>("UI/Layout/TurnLabel");
		capturedByWhiteContainer = GetNode<HFlowContainer>("UI/Layout/CapturedByWhiteContainer");
		capturedByBlackContainer = GetNode<HFlowContainer>("UI/Layout/CapturedByBlackContainer");
		restartButton = GetNode<Button>("UI/Layout/RestartButton");
		undoButton = GetNode<Button>("UI/Layout/UndoButton");
		gameOverPanel = GetNode<Panel>("UI/GameOverPanel");
		resultLabel = GetNode<Label>("UI/GameOverPanel/ResultLayout/ResultLabel");
		moveHistoryLabel = GetNode<Label>("UI/Layout/MoveHistoryScroll/MoveHistoryLabel");
		loadButton = GetNode<Button>("UI/Layout/LoadButton");
		gameLayout = GetNode<Control>("UI/Layout");
		levelSelectPanel = GetNode<Panel>("UI/LevelSelectPanel");
		humanVsHumanButton = GetNode<Button>("UI/LevelSelectPanel/MenuLayout/LevelButtons/HumanVsHumanButton");
		beginnerButton = GetNode<Button>("UI/LevelSelectPanel/MenuLayout/LevelButtons/BeginnerButton");
		intermediateButton = GetNode<Button>("UI/LevelSelectPanel/MenuLayout/LevelButtons/IntermediateButton");
		advancedButton = GetNode<Button>("UI/LevelSelectPanel/MenuLayout/LevelButtons/AdvancedButton");
		masterButton = GetNode<Button>("UI/LevelSelectPanel/MenuLayout/LevelButtons/MasterButton");
		grandMasterButton = GetNode<Button>("UI/LevelSelectPanel/MenuLayout/LevelButtons/GrandMasterButton");
		levelSelectLoadButton = GetNode<Button>("UI/LevelSelectPanel/MenuLayout/LoadButton");
		mainMenuButton = GetNode<Button>("UI/GameOverPanel/ResultLayout/ResultLabel/MainMenuButton");
		exitButton = GetNode<Button>("UI/GameOverPanel/ResultLayout/ResultLabel/ExitButton");

		// ---- Initial visibility: only the level-select screen shows on launch ----
		gameOverPanel.Visible = false;
		board.Visible = false;
		gameLayout.Visible = false;
		levelSelectPanel.Visible = true;

		// ---- Level-select button wiring ----
		// Each level button starts a brand new game with AI enabled at
		// that specific difficulty; Human vs Human starts one with AI
		// disabled entirely (the difficulty argument is irrelevant in that
		// case since StartGame's enableAi=false short-circuits any AI logic).
		humanVsHumanButton.Pressed += () => StartGame(false, AiDifficulty.Beginner);
		beginnerButton.Pressed += () => StartGame(true, AiDifficulty.Beginner);
		intermediateButton.Pressed += () => StartGame(true, AiDifficulty.Intermediate);
		advancedButton.Pressed += () => StartGame(true, AiDifficulty.Advanced);
		masterButton.Pressed += () => StartGame(true, AiDifficulty.Master);
		grandMasterButton.Pressed += () => StartGame(true, AiDifficulty.GrandMaster);
		levelSelectLoadButton.Pressed += OnLevelSelectLoadPressed;
		loadButton.Pressed += OnLoadPressed;

		// ---- Board event subscriptions ----
		board.TurnChanged += OnTurnChanged;
		board.GameOver += OnGameOver;
		board.PieceCaptured += OnPieceCaptured;
		board.GameStateRefreshed += OnGameStateRefreshed;
		undoButton.Pressed += OnUndoPressed;
		restartButton.Pressed += OnRestartPressed;
		board.CheckStatusChanged += OnCheckStatusChanged;
		board.MoveRecorded += OnMoveRecorded;
		turnLabel.Text = "White to move";
		mainMenuButton.Pressed += OnMainMenuPressed;
		exitButton.Pressed += OnExitPressed;
	}
	
	


	private void OnTurnChanged(PieceColor newColor)
	{
		turnLabel.Text = newColor == PieceColor.White ? "White to move" : "Black to move";
	}
	
	// Spawns one small icon representing a captured piece into the given
	// container (the white-captures row or black-captures row).
	private void AddCapturedIcon(Piece piece, Container container)
{
	var icon = new TextureRect();
	icon.Texture = board.GetPieceTexture(piece.Type, piece.Color);
	icon.CustomMinimumSize = new Vector2(48, 48);
	icon.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
	icon.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
	container.AddChild(icon);
}

private void OnPieceCaptured(PieceType capturedType, PieceColor capturedColor, PieceColor byColor)
{
	var piece = new Piece { Type = capturedType, Color = capturedColor };
	AddCapturedIcon(piece, byColor == PieceColor.White ? capturedByWhiteContainer : capturedByBlackContainer);
}
	
	private void OnCheckStatusChanged(bool inCheck)
	{
		turnLabel.Modulate = inCheck ? Colors.Red : Colors.White;
	}
	
	
	private void OnGameOver(string result, PieceColor? winner)
	{
		gameOverPanel.Visible = true;
		resultLabel.Text = result == "Checkmate" ? $"Checkmate — {winner} wins" : "Stalemate — Draw";
	}

private void OnUndoPressed()
{
	board.UndoLastMove();
}

// Fired by Board whenever a full state refresh is needed — after Undo,
// Restart, or Load. Rebuilds every piece of UI (turn label, check
// coloring, both captured-piece rows, and the entire move history text)
// from Board's current state, rather than trying to patch just what
// changed. This single handler is what let Load/Undo/Restart all reuse
// the exact same UI-refresh logic instead of each needing its own.
private void OnGameStateRefreshed()
{
	gameOverPanel.Visible = false;
	turnLabel.Text = board.CurrentTurn == PieceColor.White ? "White to move" : "Black to move";
	turnLabel.Modulate = board.IsCurrentPlayerInCheck() ? Colors.Red : Colors.White;

	foreach (Node child in capturedByWhiteContainer.GetChildren()) child.QueueFree();
	foreach (Node child in capturedByBlackContainer.GetChildren()) child.QueueFree();
	foreach (var piece in board.GetCapturedByWhite()) AddCapturedIcon(piece, capturedByWhiteContainer);
	foreach (var piece in board.GetCapturedByBlack()) AddCapturedIcon(piece, capturedByBlackContainer);

	moveHistoryLabel.Text = "";
	moveNumber = 1;
	isWhiteMove = true;
	foreach (var notation in board.GetMoveHistory()) OnMoveRecorded(notation);
}

	// Returns to the level-select screen from the game-over panel, mirroring
	// the exact visibility state _Ready() sets up initially.
	private void OnMainMenuPressed()
	{
		gameOverPanel.Visible = false;
		board.Visible = false;
		gameLayout.Visible = false;
		levelSelectPanel.Visible = true;
	}

	private void OnExitPressed()
	{
		GetTree().Quit();
	}

	private void OnRestartPressed()
	{
		board.ResetGame();
	}
	
	private void OnLoadPressed()
	{
		board.LoadGame();
	}
	
	// Switches from the level-select screen into the actual game view, then
	// tells Board to set up and begin a new game with the chosen settings.
	private void StartGame(bool enableAi, AiDifficulty difficulty)
	{
		levelSelectPanel.Visible = false;
		board.Visible = true;
		gameLayout.Visible = true;
		board.StartNewGame(enableAi, difficulty);
	}

	// Same screen-switch as StartGame, but resumes a saved game instead of
	// starting a fresh one.
	private void OnLevelSelectLoadPressed()
	{
		levelSelectPanel.Visible = false;
		board.Visible = true;
		gameLayout.Visible = true;
		board.LoadGame();
	}
	
	// Appends one move's notation to the move history label, formatted as
	// "1. e4 e5\n2. Nf3 Nc6\n..." — White's move starts a new numbered
	// pair, Black's move completes the line and advances the move counter.
	private void OnMoveRecorded(string notation)
	{
		if (isWhiteMove)
		moveHistoryLabel.Text += $"{moveNumber}. {notation} ";
	else
	{
		moveHistoryLabel.Text += $"{notation}\n";
		moveNumber++;
	}
	isWhiteMove = !isWhiteMove;
	}
}
