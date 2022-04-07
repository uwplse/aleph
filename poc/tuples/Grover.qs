namespace grover {
    open Microsoft.Quantum.Diagnostics;
    open Microsoft.Quantum.Canon;
    open Microsoft.Quantum.Intrinsic;
    open Microsoft.Quantum.Math;
    open Microsoft.Quantum.Measurement;
    open Microsoft.Quantum.Convert;

    operation Apply(oracle: (Qubit[], Qubit) => Unit is Adj, register : Qubit[], answers: Int) : Unit 
    is Adj {
        let n = Length(register);

        let iterations = GroverIterationsCount(n, answers);        

        for i in 1..iterations {
            GroverIteration(_toPhaseFlip(oracle, _), register);
        }
    }

    function GroverIterationsCount(n: Int, answers: Int) : Int {
        let domain = (1 <<< n);
        let iterations = Floor(PI() * Sqrt(IntAsDouble(domain / answers)) / 4.0);
        log.Debug($"Grover. iterations:{iterations} (n: {n}, a: {answers})");
        return iterations;
    }

    operation GroverOperator (register : Qubit[]) : Unit is Adj {
        // ...
        let n = Length(register);
        within {
            ApplyToEachA(H, register);
            ApplyToEachA(X, register);
        } apply {
            Controlled Z(register[1..n-1], register[0]);
        }
    }
    
    operation GroverIteration (oracle : (Qubit[] => Unit is Adj), register : Qubit[]) : Unit 
    is Adj {
        oracle(register);
        GroverOperator(register);

        if (log.DEBUG_ON()) {
            DumpMachine(());
        }
    }
    
    operation _toPhaseFlip (oracle : (Qubit[], Qubit) => Unit is Adj, register: Qubit[]) : Unit 
    is Adj {
        use target = Qubit() {
            within {
                X(target);
                H(target);
            } apply {
                oracle(register, target);
            }
        }
    }
}