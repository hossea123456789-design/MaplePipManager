using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace CropPipViewer;

public static class BurstReadySoundService
{
    private static readonly object PlayerGate = new();
    private static readonly List<MediaPlayer> ActivePlayers = new();

    public static void Play(BurstRole role, BurstReadySoundPreset preset)
    {
        if (preset == BurstReadySoundPreset.None) return;

        try
        {
            if (preset == BurstReadySoundPreset.Voice)
            {
                SpeakRoleReady(role);
                return;
            }

            var fileName = preset switch
            {
                BurstReadySoundPreset.Doorbell => "doorbell.mp3",
                BurstReadySoundPreset.DogBark => "dog-bark.mp3",
                BurstReadySoundPreset.Rooster => "rooster.mp3",
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(fileName)) return;
            var path = Path.Combine(AppContext.BaseDirectory, "assets", "sounds", fileName);
            if (!File.Exists(path))
            {
                SettingsService.Log($"burst_ready_sound_missing | role={role} | preset={preset} | path={path}");
                System.Media.SystemSounds.Asterisk.Play();
                return;
            }

            PlayMediaFile(path, role, preset);
        }
        catch (Exception ex)
        {
            SettingsService.Log($"burst_ready_sound_exception | role={role} | preset={preset} | {ex}");
        }
    }

    private static void PlayMediaFile(string path, BurstRole role, BurstReadySoundPreset preset)
    {
        var player = new MediaPlayer
        {
            Volume = 1.0
        };

        void Cleanup()
        {
            try
            {
                player.Stop();
                player.Close();
            }
            catch
            {
                // Ignore media cleanup races.
            }

            lock (PlayerGate)
            {
                ActivePlayers.Remove(player);
            }
        }

        player.MediaEnded += (_, _) => Cleanup();
        player.MediaFailed += (_, e) =>
        {
            SettingsService.Log($"burst_ready_sound_media_failed | role={role} | preset={preset} | error={e.ErrorException?.Message}");
            Cleanup();
        };

        lock (PlayerGate)
        {
            ActivePlayers.Add(player);
        }

        player.Open(new Uri(path, UriKind.Absolute));
        player.Play();
        SettingsService.Log($"burst_ready_sound_play | role={role} | preset={preset} | file={Path.GetFileName(path)}");
    }

    private static void SpeakRoleReady(BurstRole role)
    {
        var text = role switch
        {
            BurstRole.SemiBurst => "준극딜 돌았습니다",
            BurstRole.OriginBurst => "오리진 돌았습니다",
            _ => "극딜 돌았습니다"
        };

        _ = Task.Run(() =>
        {
            object? voice = null;
            try
            {
                var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (voiceType == null)
                {
                    SettingsService.Log("burst_ready_voice_unavailable | SAPI.SpVoice not found");
                    System.Media.SystemSounds.Asterisk.Play();
                    return;
                }

                voice = Activator.CreateInstance(voiceType);
                if (voice == null) return;

                voiceType.InvokeMember(
                    "Speak",
                    BindingFlags.InvokeMethod,
                    binder: null,
                    target: voice,
                    args: new object[] { text, 0 });

                SettingsService.Log($"burst_ready_voice_play | role={role} | text={text}");
            }
            catch (Exception ex)
            {
                SettingsService.Log($"burst_ready_voice_exception | role={role} | {ex}");
            }
            finally
            {
                if (voice != null && Marshal.IsComObject(voice))
                {
                    try { Marshal.FinalReleaseComObject(voice); } catch { }
                }
            }
        });
    }
}
