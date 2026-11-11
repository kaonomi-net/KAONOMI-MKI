using System;

namespace KAONOMI.Chat.Views;

/// <summary>3D animation of the Kaonomi band, driven by the voice and the streamed words.</summary>
public interface IBandScene
{
    /// <summary>True while the answer is read aloud: the scene follows <see cref="VoiceLevel"/>.</summary>
    bool IsSpeaking { get; set; }

    /// <summary>Loudness of the voice right now (0 to 1), read on every frame from the render thread.</summary>
    Func<float>? VoiceLevel { get; set; }

    /// <summary>A streamed word: the scene reacts, more for longer words.</summary>
    void Pulse(string word);
}
