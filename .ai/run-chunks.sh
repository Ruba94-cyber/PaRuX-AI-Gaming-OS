#!/bin/bash
cd /c/Users/paolo/Desktop/PaRuX
out=.ai/logs/chunks; mkdir -p $out; : > $out/summary.txt
for L in A B C D E F G H I J K L M N O P Q R S T U V W X Y Z; do
  dotnet test tests/PaRuX.Server.Tests -c Release --no-build -nologo --filter "FullyQualifiedName~PaRuX.Server.Tests.$L" > $out/$L.log 2>&1
  echo "$L exit=$? $(grep -E 'Superato!|Non superato!|nessun test|No test' $out/$L.log | tail -1 | cut -c1-120) $(grep -c 'interrotta\|terminato in modo' $out/$L.log)crash" >> $out/summary.txt
done
echo DONE >> $out/summary.txt
