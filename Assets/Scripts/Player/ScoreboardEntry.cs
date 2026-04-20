using UnityEngine;
using TMPro;

public class ScoreboardEntry : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI _nameText;
    [SerializeField] private TextMeshProUGUI _eliminationsText;
    [SerializeField] private TextMeshProUGUI _deathsText;

    private PlayerController _player;

    public void Initialize(PlayerController player)
    {
        _player = player;
        _nameText.text = player.GetPlayerName();
    }

    private void Update()
    {
        if (_player != null)
        {
            _eliminationsText.text = _player.GetEliminations().ToString();
            _deathsText.text = _player.GetDeaths().ToString();
        }
    }
}