using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Deduplicator.Models;

public class AlgorithmParameters : INotifyPropertyChanged
{
    private double _threshold = 0.85;
    private int _windowSize = 11;
    private int _hashSize = 8;
    private double _hashWeight = 0.5;
    private int _blockSize = 200;
    private int _bins = 256;
    private int _thumbSize = 64;
    private int _features = 1000;
    private int _minMatch = 6;
    private int _dim = 128;
    private int _knn = 5;
    private string _sortMethod = "filename";
    private string _hashAlgorithm = "MD5";
    private int _hessianThreshold = 400;

    public double Threshold
    {
        get => _threshold;
        set
        {
            if (_threshold != value)
            {
                _threshold = value;
                OnPropertyChanged();
            }
        }
    }

    public int WindowSize
    {
        get => _windowSize;
        set
        {
            if (_windowSize != value)
            {
                _windowSize = value;
                OnPropertyChanged();
            }
        }
    }

    public int HashSize
    {
        get => _hashSize;
        set
        {
            if (_hashSize != value)
            {
                _hashSize = value;
                OnPropertyChanged();
            }
        }
    }

    public double HashWeight
    {
        get => _hashWeight;
        set
        {
            if (_hashWeight != value)
            {
                _hashWeight = value;
                OnPropertyChanged();
            }
        }
    }

    public int BlockSize
    {
        get => _blockSize;
        set
        {
            if (_blockSize != value)
            {
                _blockSize = value;
                OnPropertyChanged();
            }
        }
    }

    public int Bins
    {
        get => _bins;
        set
        {
            if (_bins != value)
            {
                _bins = value;
                OnPropertyChanged();
            }
        }
    }

    public int ThumbSize
    {
        get => _thumbSize;
        set
        {
            if (_thumbSize != value)
            {
                _thumbSize = value;
                OnPropertyChanged();
            }
        }
    }

    public int Features
    {
        get => _features;
        set
        {
            if (_features != value)
            {
                _features = value;
                OnPropertyChanged();
            }
        }
    }

    public int MinMatch
    {
        get => _minMatch;
        set
        {
            if (_minMatch != value)
            {
                _minMatch = value;
                OnPropertyChanged();
            }
        }
    }

    public int Dim
    {
        get => _dim;
        set
        {
            if (_dim != value)
            {
                _dim = value;
                OnPropertyChanged();
            }
        }
    }

    public int Knn
    {
        get => _knn;
        set
        {
            if (_knn != value)
            {
                _knn = value;
                OnPropertyChanged();
            }
        }
    }

    public string SortMethod
    {
        get => _sortMethod;
        set
        {
            if (_sortMethod != value)
            {
                _sortMethod = value;
                OnPropertyChanged();
            }
        }
    }

    public string HashAlgorithm
    {
        get => _hashAlgorithm;
        set
        {
            if (_hashAlgorithm != value)
            {
                _hashAlgorithm = value;
                OnPropertyChanged();
            }
        }
    }

    public int HessianThreshold
    {
        get => _hessianThreshold;
        set
        {
            if (_hessianThreshold != value)
            {
                _hessianThreshold = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}