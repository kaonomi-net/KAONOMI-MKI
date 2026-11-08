using System;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.OpenGL;

namespace KAONOMI.UI.Avatar;

/// <summary>
/// The OpenGL (ES 3 / 3.3) functions the 3D animations use, read from the context of Avalonia with
/// <see cref="GlInterface.GetProcAddress"/>: the same calls on ANGLE (Windows), desktop OpenGL (Linux) and macOS.
/// </summary>
internal sealed unsafe class Gl
{
    public const int VertexShader = 0x8B31, FragmentShader = 0x8B30, CompileStatus = 0x8B81, LinkStatus = 0x8B82, InfoLogLength = 0x8B84;
    public const int ArrayBuffer = 0x8892, ElementArrayBuffer = 0x8893, StaticDraw = 0x88E4;
    public const int Float = 0x1406, UnsignedInt = 0x1405, UnsignedByte = 0x1401, Triangles = 0x0004, TriangleStrip = 0x0005;
    public const int Texture2D = 0x0DE1, Texture0 = 0x84C0, Rgba = 0x1908, Linear = 0x2601, LinearMipmapLinear = 0x2703;
    public const int TextureMinFilter = 0x2801, TextureMagFilter = 0x2800, TextureWrapS = 0x2802, TextureWrapT = 0x2803, ClampToEdge = 0x812F;
    public const int Blend = 0x0BE2, DepthTest = 0x0B71, CullFace = 0x0B44, One = 1, OneMinusSrcAlpha = 0x0303;
    public const int ColorBufferBit = 0x4000, DepthBufferBit = 0x0100, Framebuffer = 0x8D40, Lines = 0x0001, Less = 0x0201, LessEqual = 0x0203, SrcAlpha = 0x0302, PackAlignment = 0x0D05;

    private readonly delegate* unmanaged<int, int> _createShader;
    private readonly delegate* unmanaged<int, int, byte**, int*, void> _shaderSource;
    private readonly delegate* unmanaged<int, void> _compileShader;
    private readonly delegate* unmanaged<int, int, int*, void> _getShaderiv;
    private readonly delegate* unmanaged<int, int, int*, byte*, void> _getShaderInfoLog;
    private readonly delegate* unmanaged<int> _createProgram;
    private readonly delegate* unmanaged<int, int, void> _attachShader;
    private readonly delegate* unmanaged<int, int, byte*, void> _bindAttribLocation;
    private readonly delegate* unmanaged<int, void> _linkProgram;
    private readonly delegate* unmanaged<int, int, int*, void> _getProgramiv;
    private readonly delegate* unmanaged<int, int, int*, byte*, void> _getProgramInfoLog;
    private readonly delegate* unmanaged<int, void> _useProgram, _deleteProgram, _deleteShader, _bindVertexArray, _enableVertexAttribArray, _enable, _disable, _clear, _activeTexture, _generateMipmap;
    private readonly delegate* unmanaged<int, byte*, int> _getUniformLocation;
    private readonly delegate* unmanaged<int, float, void> _uniform1f;
    private readonly delegate* unmanaged<int, int, void> _uniform1i, _bindBuffer, _bindTexture, _blendFunc, _bindFramebuffer;
    private readonly delegate* unmanaged<int, float, float, void> _uniform2f;
    private readonly delegate* unmanaged<int, float, float, float, void> _uniform3f;
    private readonly delegate* unmanaged<int, float, float, float, float, void> _uniform4f;
    private readonly delegate* unmanaged<int, int, byte, float*, void> _uniformMatrix4fv;
    private readonly delegate* unmanaged<int, int*, void> _genBuffers, _genVertexArrays, _genTextures;
    private readonly delegate* unmanaged<int, int*, void> _deleteBuffers, _deleteVertexArrays, _deleteTextures;
    private readonly delegate* unmanaged<int, nint, void*, int, void> _bufferData;
    private readonly delegate* unmanaged<int, int, int, byte, int, nint, void> _vertexAttribPointer;
    private readonly delegate* unmanaged<int, int, int, nint, void> _drawElements;
    private readonly delegate* unmanaged<int, int, int, void> _drawArrays, _texParameteri;
    private readonly delegate* unmanaged<int, int, int, int, void> _viewport;
    private readonly delegate* unmanaged<float, float, float, float, void> _clearColor;
    private readonly delegate* unmanaged<int, int, int, int, int, int, int, int, void*, void> _texImage2D;
    private readonly delegate* unmanaged<byte, void> _depthMask;
    private readonly delegate* unmanaged<int, void> _depthFunc;
    private readonly delegate* unmanaged<int, int, int, int, int, int, void*, void> _readPixels;
    private readonly delegate* unmanaged<int, int, void> _pixelStorei;

    public Gl(GlInterface gl)
    {
        IsEs = gl.ContextInfo.Version.Type == GlProfileType.OpenGLES;
        nint F(string name)
        {
            var address = gl.GetProcAddress(name);
            return address != 0 ? address : throw new InvalidOperationException($"OpenGL function {name} not found");
        }
        _createShader = (delegate* unmanaged<int, int>)F("glCreateShader");
        _shaderSource = (delegate* unmanaged<int, int, byte**, int*, void>)F("glShaderSource");
        _compileShader = (delegate* unmanaged<int, void>)F("glCompileShader");
        _getShaderiv = (delegate* unmanaged<int, int, int*, void>)F("glGetShaderiv");
        _getShaderInfoLog = (delegate* unmanaged<int, int, int*, byte*, void>)F("glGetShaderInfoLog");
        _createProgram = (delegate* unmanaged<int>)F("glCreateProgram");
        _attachShader = (delegate* unmanaged<int, int, void>)F("glAttachShader");
        _bindAttribLocation = (delegate* unmanaged<int, int, byte*, void>)F("glBindAttribLocation");
        _linkProgram = (delegate* unmanaged<int, void>)F("glLinkProgram");
        _getProgramiv = (delegate* unmanaged<int, int, int*, void>)F("glGetProgramiv");
        _getProgramInfoLog = (delegate* unmanaged<int, int, int*, byte*, void>)F("glGetProgramInfoLog");
        _useProgram = (delegate* unmanaged<int, void>)F("glUseProgram");
        _deleteProgram = (delegate* unmanaged<int, void>)F("glDeleteProgram");
        _deleteShader = (delegate* unmanaged<int, void>)F("glDeleteShader");
        _getUniformLocation = (delegate* unmanaged<int, byte*, int>)F("glGetUniformLocation");
        _uniform1f = (delegate* unmanaged<int, float, void>)F("glUniform1f");
        _uniform1i = (delegate* unmanaged<int, int, void>)F("glUniform1i");
        _uniform2f = (delegate* unmanaged<int, float, float, void>)F("glUniform2f");
        _uniform3f = (delegate* unmanaged<int, float, float, float, void>)F("glUniform3f");
        _uniform4f = (delegate* unmanaged<int, float, float, float, float, void>)F("glUniform4f");
        _uniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)F("glUniformMatrix4fv");
        _genBuffers = (delegate* unmanaged<int, int*, void>)F("glGenBuffers");
        _deleteBuffers = (delegate* unmanaged<int, int*, void>)F("glDeleteBuffers");
        _genVertexArrays = (delegate* unmanaged<int, int*, void>)F("glGenVertexArrays");
        _deleteVertexArrays = (delegate* unmanaged<int, int*, void>)F("glDeleteVertexArrays");
        _bindVertexArray = (delegate* unmanaged<int, void>)F("glBindVertexArray");
        _bindBuffer = (delegate* unmanaged<int, int, void>)F("glBindBuffer");
        _bufferData = (delegate* unmanaged<int, nint, void*, int, void>)F("glBufferData");
        _vertexAttribPointer = (delegate* unmanaged<int, int, int, byte, int, nint, void>)F("glVertexAttribPointer");
        _enableVertexAttribArray = (delegate* unmanaged<int, void>)F("glEnableVertexAttribArray");
        _drawElements = (delegate* unmanaged<int, int, int, nint, void>)F("glDrawElements");
        _drawArrays = (delegate* unmanaged<int, int, int, void>)F("glDrawArrays");
        _viewport = (delegate* unmanaged<int, int, int, int, void>)F("glViewport");
        _clearColor = (delegate* unmanaged<float, float, float, float, void>)F("glClearColor");
        _clear = (delegate* unmanaged<int, void>)F("glClear");
        _enable = (delegate* unmanaged<int, void>)F("glEnable");
        _disable = (delegate* unmanaged<int, void>)F("glDisable");
        _blendFunc = (delegate* unmanaged<int, int, void>)F("glBlendFunc");
        _bindFramebuffer = (delegate* unmanaged<int, int, void>)F("glBindFramebuffer");
        _genTextures = (delegate* unmanaged<int, int*, void>)F("glGenTextures");
        _deleteTextures = (delegate* unmanaged<int, int*, void>)F("glDeleteTextures");
        _bindTexture = (delegate* unmanaged<int, int, void>)F("glBindTexture");
        _activeTexture = (delegate* unmanaged<int, void>)F("glActiveTexture");
        _texImage2D = (delegate* unmanaged<int, int, int, int, int, int, int, int, void*, void>)F("glTexImage2D");
        _texParameteri = (delegate* unmanaged<int, int, int, void>)F("glTexParameteri");
        _generateMipmap = (delegate* unmanaged<int, void>)F("glGenerateMipmap");
        _depthMask = (delegate* unmanaged<byte, void>)F("glDepthMask");
        _depthFunc = (delegate* unmanaged<int, void>)F("glDepthFunc");
        _readPixels = (delegate* unmanaged<int, int, int, int, int, int, void*, void>)F("glReadPixels");
        _pixelStorei = (delegate* unmanaged<int, int, void>)F("glPixelStorei");
    }

    /// <summary>True on OpenGL ES (ANGLE on Windows), false on desktop OpenGL.</summary>
    public bool IsEs { get; }

    /// <summary>The first lines of every shader: the version of GLSL of the context and, on ES, the precision.</summary>
    public string Header => IsEs ? "#version 300 es\nprecision highp float;\nprecision highp int;\n" : "#version 330 core\n";

    /// <summary>A program of <paramref name="vertex"/> and <paramref name="fragment"/>, its attributes at the locations given.</summary>
    public int Program(string vertex, string fragment, params string[] attributes)
    {
        var vs = Shader(VertexShader, vertex);
        var fs = Shader(FragmentShader, fragment);
        var program = _createProgram();
        _attachShader(program, vs);
        _attachShader(program, fs);
        for (var i = 0; i < attributes.Length; i++)
        {
            var name = Encoding.ASCII.GetBytes(attributes[i] + "\0");
            fixed (byte* p = name)
                _bindAttribLocation(program, i, p);
        }
        _linkProgram(program);
        _deleteShader(vs);
        _deleteShader(fs);
        int ok;
        _getProgramiv(program, LinkStatus, &ok);
        if (ok == 0)
        {
            var log = Log(program, _getProgramiv, _getProgramInfoLog);
            _deleteProgram(program);
            throw new InvalidOperationException("Link: " + log);
        }
        return program;
    }

    private int Shader(int type, string source)
    {
        var shader = _createShader(type);
        var bytes = Encoding.UTF8.GetBytes(source);
        fixed (byte* p = bytes)
        {
            var pointer = p;
            var length = bytes.Length;
            _shaderSource(shader, 1, &pointer, &length);
        }
        _compileShader(shader);
        int ok;
        _getShaderiv(shader, CompileStatus, &ok);
        if (ok == 0)
        {
            var log = Log(shader, _getShaderiv, _getShaderInfoLog);
            _deleteShader(shader);
            throw new InvalidOperationException((type == VertexShader ? "Vertex shader: " : "Fragment shader: ") + log);
        }
        return shader;
    }

    private static string Log(int id, delegate* unmanaged<int, int, int*, void> get, delegate* unmanaged<int, int, int*, byte*, void> read)
    {
        int length;
        get(id, InfoLogLength, &length);
        if (length <= 1)
            return "(no log)";
        var buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            int written;
            read(id, length, &written, p);
            return Encoding.UTF8.GetString(buffer, 0, Math.Max(0, written));
        }
    }

    public int Uniform(int program, string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes)
            return _getUniformLocation(program, p);
    }

    public void UseProgram(int program) => _useProgram(program);
    public void DeleteProgram(int program) => _deleteProgram(program);
    public void Uniform1(int location, float value) => _uniform1f(location, value);
    public void Uniform1(int location, int value) => _uniform1i(location, value);
    public void Uniform2(int location, float x, float y) => _uniform2f(location, x, y);
    public void Uniform3(int location, float x, float y, float z) => _uniform3f(location, x, y, z);
    public void Uniform4(int location, float x, float y, float z, float w) => _uniform4f(location, x, y, z, w);

    public void UniformMatrix4(int location, System.Numerics.Matrix4x4 matrix)
    {
        // Row vectors in System.Numerics: its rows read as the columns of the matrix of the shader.
        var m = matrix;
        _uniformMatrix4fv(location, 1, 0, (float*)&m);
    }

    public int GenBuffer()
    {
        int id;
        _genBuffers(1, &id);
        return id;
    }

    public void DeleteBuffer(int id) => _deleteBuffers(1, &id);

    public int GenVertexArray()
    {
        int id;
        _genVertexArrays(1, &id);
        return id;
    }

    public void DeleteVertexArray(int id) => _deleteVertexArrays(1, &id);
    public void BindVertexArray(int id) => _bindVertexArray(id);
    public void BindBuffer(int target, int id) => _bindBuffer(target, id);

    public void BufferData<T>(int target, T[] data) where T : unmanaged
    {
        fixed (T* p = data)
            _bufferData(target, (nint)(data.Length * sizeof(T)), p, StaticDraw);
    }

    public void VertexAttribPointer(int index, int size, int stride, int offset)
    {
        _vertexAttribPointer(index, size, Float, 0, stride, offset);
        _enableVertexAttribArray(index);
    }

    public void DrawElements(int mode, int count) => _drawElements(mode, count, UnsignedInt, 0);
    public void DrawArrays(int mode, int first, int count) => _drawArrays(mode, first, count);
    public void Viewport(int x, int y, int width, int height) => _viewport(x, y, width, height);

    public void Clear(float r, float g, float b, float a, bool depth = false)
    {
        _clearColor(r, g, b, a);
        _clear(depth ? ColorBufferBit | DepthBufferBit : ColorBufferBit);
    }

    public void DepthMask(bool write) => _depthMask(write ? (byte)1 : (byte)0);
    public void DepthFunc(int function) => _depthFunc(function);

    /// <summary>The pixels of the framebuffer bound, RGBA, bottom row first (as OpenGL reads them).</summary>
    public byte[] ReadPixels(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        _pixelStorei(PackAlignment, 1);
        fixed (byte* p = pixels)
            _readPixels(0, 0, width, height, Rgba, UnsignedByte, p);
        return pixels;
    }

    public void Enable(int capability) => _enable(capability);
    public void Disable(int capability) => _disable(capability);
    public void BlendFunc(int source, int destination) => _blendFunc(source, destination);
    public void BindFramebuffer(int framebuffer) => _bindFramebuffer(Framebuffer, framebuffer);

    /// <summary>A texture of <paramref name="rgba"/> (premultiplied RGBA, top row first), with mipmaps.</summary>
    public int Texture(byte[] rgba, int width, int height)
    {
        int id;
        _genTextures(1, &id);
        _activeTexture(Texture0);
        _bindTexture(Texture2D, id);
        fixed (byte* p = rgba)
            _texImage2D(Texture2D, 0, Rgba, width, height, 0, Rgba, UnsignedByte, p);
        _generateMipmap(Texture2D);
        _texParameteri(Texture2D, TextureMinFilter, LinearMipmapLinear);
        _texParameteri(Texture2D, TextureMagFilter, Linear);
        _texParameteri(Texture2D, TextureWrapS, ClampToEdge);
        _texParameteri(Texture2D, TextureWrapT, ClampToEdge);
        return id;
    }

    public void BindTexture(int id)
    {
        _activeTexture(Texture0);
        _bindTexture(Texture2D, id);
    }

    public void DeleteTexture(int id) => _deleteTextures(1, &id);
}
