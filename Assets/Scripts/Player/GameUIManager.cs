using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using Unity.Netcode;
using System.Collections;

public class GameUIManager : NetworkBehaviour
{
    public static GameUIManager Instance { get; private set; }

    [Header("Player HUD")]
    [SerializeField] private Slider _healthSlider;
    [SerializeField] private TextMeshProUGUI _healthText;
    [SerializeField] private TextMeshProUGUI _eliminationsText;
    [SerializeField] private TextMeshProUGUI _timerText;
    [SerializeField] private GameObject _deathScreen;
    [SerializeField] private TextMeshProUGUI _respawnTimerText;

    [Header("Scoreboard")]
    [SerializeField] private GameObject _scoreboardPanel;
    [SerializeField] private Transform _scoreboardContent;
    [SerializeField] private GameObject _scoreboardEntryPrefab;

    [Header("Game Settings")]
    [SerializeField] private float _gameDuration = 120f;
    [SerializeField] private GameObject _gameOverScreen;
    [SerializeField] private TextMeshProUGUI _winnerText;

    private NetworkVariable<float> _gameTimer = new NetworkVariable<float>(0f);
    private Dictionary<ulong, ScoreboardEntry> _scoreboardEntries = new Dictionary<ulong, ScoreboardEntry>();
    private float _respawnTimer;
    private bool _isDead;
    private PlayerController _localPlayer;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // Find local player after a frame
        StartCoroutine(FindLocalPlayerDelayed());
    }

    private IEnumerator FindLocalPlayerDelayed()
    {
        yield return new WaitForSeconds(0.1f);
        FindLocalPlayer();
    }

    private void FindLocalPlayer()
    {
        PlayerController[] players = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
        foreach (var player in players)
        {
            if (player.IsOwner)
            {
                _localPlayer = player;
                SetupPlayerUI();
                break;
            }
        }
    }

    private void SetupPlayerUI()
    {
        if (_localPlayer != null)
        {
            UpdateHealth(_localPlayer.GetHealth(), _localPlayer.GetMaxHealth());
            UpdateEliminations(_localPlayer.GetEliminations());
        }
    }

    public override void OnNetworkSpawn()
    {
        _gameTimer.OnValueChanged += OnGameTimerChanged;

        if (IsServer)
        {
            _gameTimer.Value = _gameDuration;
        }
    }

    public override void OnNetworkDespawn()
    {
        _gameTimer.OnValueChanged -= OnGameTimerChanged;
    }

    private void Update()
    {
        if (IsServer)
        {
            if (_gameTimer.Value > 0 && GameManager.Instance != null && GameManager.Instance.GameStarted.Value)
            {
                _gameTimer.Value = Mathf.Max(0, _gameTimer.Value - Time.deltaTime);

                if (_gameTimer.Value <= 0)
                {
                    EndGame();
                }
            }
        }

        UpdateTimerDisplay();

        if (_isDead)
        {
            _respawnTimer -= Time.deltaTime;
            UpdateRespawnTimerDisplay();
        }

        if (Input.GetKeyDown(KeyCode.Tab))
        {
            ShowScoreboard();
        }
        else if (Input.GetKeyUp(KeyCode.Tab))
        {
            HideScoreboard();
        }

        // Refresh local player reference if needed
        if (_localPlayer == null && Time.frameCount % 60 == 0)
        {
            FindLocalPlayer();
        }
    }

    private void UpdateTimerDisplay()
    {
        if (_timerText != null)
        {
            int minutes = Mathf.FloorToInt(_gameTimer.Value / 60);
            int seconds = Mathf.FloorToInt(_gameTimer.Value % 60);
            _timerText.text = $"{minutes:00}:{seconds:00}";
        }
    }

    private void OnGameTimerChanged(float previous, float current)
    {
        UpdateTimerDisplay();
    }

    public void RegisterPlayer(PlayerController player)
    {
        if (_scoreboardEntryPrefab != null && _scoreboardContent != null)
        {
            GameObject entryObj = Instantiate(_scoreboardEntryPrefab, _scoreboardContent);
            ScoreboardEntry entry = entryObj.GetComponent<ScoreboardEntry>();
            if (entry != null)
            {
                entry.Initialize(player);
                _scoreboardEntries[player.OwnerClientId] = entry;
            }
        }

        // If this is our local player, set up HUD
        if (player.IsOwner)
        {
            _localPlayer = player;
            SetupPlayerUI();
        }
    }

    public void UnregisterPlayer(PlayerController player)
    {
        if (_scoreboardEntries.TryGetValue(player.OwnerClientId, out ScoreboardEntry entry))
        {
            Destroy(entry.gameObject);
            _scoreboardEntries.Remove(player.OwnerClientId);
        }
    }

    public void UpdateHealth(float current, float max)
    {
        if (_healthSlider != null)
        {
            _healthSlider.maxValue = max;
            _healthSlider.value = current;
        }

        if (_healthText != null)
        {
            _healthText.text = $"{current:F0}/{max:F0}";
        }
    }

    public void UpdateEliminations(int eliminations)
    {
        if (_eliminationsText != null)
        {
            _eliminationsText.text = $"Eliminations: {eliminations}";
        }
    }

    public void ShowDeathScreen()
    {
        _isDead = true;
        _respawnTimer = 5f;

        if (_deathScreen != null)
        {
            _deathScreen.SetActive(true);
        }
    }

    public void HideDeathScreen()
    {
        _isDead = false;

        if (_deathScreen != null)
        {
            _deathScreen.SetActive(false);
        }
    }

    private void UpdateRespawnTimerDisplay()
    {
        if (_respawnTimerText != null)
        {
            _respawnTimerText.text = $"Respawning in {_respawnTimer:F0}...";
        }
    }

    private void ShowScoreboard()
    {
        if (_scoreboardPanel != null)
        {
            _scoreboardPanel.SetActive(true);
        }
    }

    private void HideScoreboard()
    {
        if (_scoreboardPanel != null)
        {
            _scoreboardPanel.SetActive(false);
        }
    }

    private void EndGame()
    {
        PlayerController winner = null;
        int highestEliminations = -1;

        PlayerController[] allPlayers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);

        foreach (var player in allPlayers)
        {
            if (player.GetEliminations() > highestEliminations)
            {
                highestEliminations = player.GetEliminations();
                winner = player;
            }
        }

        if (winner != null)
        {
            ShowGameOver(winner.GetPlayerName(), highestEliminations);
        }
    }

    private void ShowGameOver(string winnerName, int eliminations)
    {
        if (_gameOverScreen != null)
        {
            _gameOverScreen.SetActive(true);
        }

        if (_winnerText != null)
        {
            _winnerText.text = $"{winnerName} wins!\n{eliminations} Eliminations";
        }

        StartCoroutine(ReturnToLobbyAfterDelay(10f));
    }

    private IEnumerator ReturnToLobbyAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);

        if (NetworkManager.Singleton.IsServer)
        {
            GameManager.Instance.GameStarted.Value = false;

            foreach (var player in FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
            {
                if (player.NetworkObject.IsSpawned)
                {
                    player.NetworkObject.Despawn();
                }
            }
        }
    }
}